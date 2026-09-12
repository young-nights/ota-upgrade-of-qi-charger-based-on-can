/**
  **************************************************************************
  * @file     qi_protocol.c
  * @brief    Qi 无线充电芯片 UART 通信协议层实现
  *
  * @details  基于 qi_uart.c 底层驱动，实现帧解析、校验、命令收发。
  *           接收端使用状态机逐字节解析，支持 ISR 写入 + 主循环读取。
  *           发送端负责组装帧头/长度/校验并通过 qi_uart_send() 发出。
  *
  *           普通帧格式：
  *           [0x55][0xAA][LEN][CMD][DATA...][SEQ][CS]
  *           LEN = CMD(1) + DATA(n) (不含 SEQ)
  *           CS = (0x55 + 0xAA + LEN + CMD + DATA[0..n-1] + SEQ) & 0xFF
  *
  *           IAP 帧格式 (Command 0xCC，不含 SEQ)：
  *           [0x55][0xAA][LEN][CMD][DATA...][CS]
  *           LEN = CMD(1) + DATA(n)
  *           CS = (0x55 + 0xAA + LEN + CMD + DATA[0..n-1]) & 0xFF
  *
  *           MCU→Qi: 55 AA 04 CC 01 [size_hi] [size_lo] [CS]  (准备升级)
  *           MCU→Qi: 55 AA xx CC 02 [addr_hi] [addr_lo] [data...] [CS]  (数据包)
  *           Qi→MCU: 55 AA 04 CC 01 [status] 00 [CS]  (应答)
  **************************************************************************
  */

/* 头文件 ------------------------------------------------------------------*/
#include "qi_protocol.h"
#include "qi_uart.h"

/* ==========================================================================
 *  私有变量
 * ========================================================================== */

/** @brief 接收状态机当前状态 */
static qi_rx_state_t rx_state = QI_RX_STATE_IDLE;

/** @brief 帧接收缓冲区 */
static qi_frame_t rx_frame;

/** @brief 接收过程中的累加校验和 */
static uint8_t rx_cs_acc;

/** @brief 当前帧期望的数据长度（从 LEN 字段解析） */
static uint8_t rx_expected_data_len;

/** @brief 已接收的数据字节数 */
static uint8_t rx_data_idx;

/** @brief 当前帧是否期望流水号 (1=普通帧, 0=IAP帧) */
static uint8_t rx_expect_seq;

/** @brief 帧接收回调函数 */
static qi_frame_callback_t frame_callback = (qi_frame_callback_t)0;

/** @brief 流水号（发送用，每次发送递增） */
static uint8_t tx_seq = 0;

/* ==========================================================================
 *  私有函数
 * ========================================================================== */

/**
 * @brief  复位接收状态机到空闲状态
 */
static void rx_reset(void)
{
  rx_state = QI_RX_STATE_IDLE;
  rx_cs_acc = 0;
  rx_expected_data_len = 0;
  rx_data_idx = 0;
  rx_expect_seq = 1U;
}

/**
 * @brief  处理一帧完整接收后的逻辑
 * @note   校验和已在接收过程中累加验证，这里直接调用回调
 */
static void rx_complete(void)
{
  if (frame_callback != (qi_frame_callback_t)0)
  {
    frame_callback(&rx_frame);
  }
  rx_reset();
}

/**
 * @brief  接收状态机处理一个字节
 * @param  byte: 从 UART 接收到的字节
 */
static void rx_process_byte(uint8_t byte)
{
  switch (rx_state)
  {
    case QI_RX_STATE_IDLE:
      /* 等待帧起始头第1字节 0x55 */
      if (byte == QI_FRAME_HEADER1)
      {
        rx_cs_acc = byte;  /* 开始累加校验和 */
        rx_state = QI_RX_STATE_HEADER2;
      }
      break;

    case QI_RX_STATE_HEADER2:
      /* 等待帧起始头第2字节 0xAA */
      if (byte == QI_FRAME_HEADER2)
      {
        rx_cs_acc += byte;
        rx_state = QI_RX_STATE_LENGTH;
      }
      else
      {
        rx_reset();  /* 帧头不匹配，重置 */
      }
      break;

    case QI_RX_STATE_LENGTH:
      /* 帧长度 = 命令(1) + 数据(n) (不含流水号) */
      rx_cs_acc += byte;
      if (byte < 1U || byte > (QI_FRAME_MAX_DATA_LEN + 1U))
      {
        rx_reset();  /* 长度异常 */
        break;
      }
      rx_expected_data_len = (uint8_t)(byte - 1U);  /* 减去命令字节 */
      rx_frame.data_len = rx_expected_data_len;
      rx_data_idx = 0;
      rx_state = QI_RX_STATE_CMD;
      break;

    case QI_RX_STATE_CMD:
      /* 命令码 */
      rx_cs_acc += byte;
      rx_frame.cmd = byte;
      rx_expect_seq = (byte == QI_CMD_IAP) ? 0U : 1U;
      rx_frame.expect_seq = rx_expect_seq;
      if (rx_expected_data_len > 0U)
      {
        rx_state = QI_RX_STATE_DATA;
      }
      else if (rx_expect_seq != 0U)
      {
        rx_state = QI_RX_STATE_SEQ;  /* 无数据，普通帧到流水号 */
      }
      else
      {
        rx_state = QI_RX_STATE_CS;   /* 无数据，IAP 帧直接到校验 */
      }
      break;

    case QI_RX_STATE_DATA:
      /* 帧数据 */
      rx_cs_acc += byte;
      if (rx_data_idx < QI_FRAME_MAX_DATA_LEN)
      {
        rx_frame.data[rx_data_idx] = byte;
      }
      rx_data_idx++;
      if (rx_data_idx >= rx_expected_data_len)
      {
        rx_state = (rx_expect_seq != 0U) ? QI_RX_STATE_SEQ : QI_RX_STATE_CS;
      }
      break;

    case QI_RX_STATE_SEQ:
      /* 流水号 */
      rx_cs_acc += byte;
      rx_frame.seq = byte;
      rx_state = QI_RX_STATE_CS;
      break;

    case QI_RX_STATE_CS:
      /* 校验和：验证 */
      rx_frame.cs = byte;
      if ((rx_cs_acc & 0xFFU) == byte)
      {
        rx_complete();  /* 校验通过，回调 */
      }
      else
      {
        rx_reset();     /* 校验失败，丢弃 */
      }
      break;

    default:
      rx_reset();
      break;
  }
}

/* ==========================================================================
 *  UART 底层回调（注册到 qi_uart）
 * ========================================================================== */

/**
 * @brief  UART 接收回调（从 qi_uart_poll 主循环调用）
 * @param  data: 接收到的数据指针
 * @param  len: 数据长度
 */
static void uart_rx_handler(uint8_t *data, uint8_t len)
{
  uint8_t i;
  for (i = 0U; i < len; i++)
  {
    rx_process_byte(data[i]);
  }
}

/* ==========================================================================
 *  导出函数
 * ========================================================================== */

/**
 * @brief  初始化 Qi 协议层
 * @note   初始化底层 UART，注册帧接收回调
 */
void qi_protocol_init(void)
{
  qi_uart_init();
  qi_uart_register_rx_callback(uart_rx_handler);
  rx_reset();
  tx_seq = 0;
}

/**
 * @brief  注册帧接收回调
 * @param  cb: 回调函数指针，收到完整校验通过的帧时调用
 */
void qi_protocol_register_callback(qi_frame_callback_t cb)
{
  frame_callback = cb;
}

/**
 * @brief  构建并发送一帧数据
 * @note   自动计算校验和，格式：[0x55][0xAA][LEN][CMD][DATA...][SEQ][CS]
 *         LEN = CMD(1) + DATA(n) (不含 SEQ)
 * @param  cmd: 命令码
 * @param  data: 帧数据指针（可为 NULL）
 * @param  data_len: 帧数据长度
 * @param  seq: 流水号
 * @retval 0=成功，-1=参数错误
 */
int8_t qi_protocol_send(uint8_t cmd, const uint8_t *data, uint8_t data_len, uint8_t seq)
{
  uint8_t buf[QI_FRAME_MIN_LEN + QI_FRAME_MAX_DATA_LEN];
  uint8_t len_field;
  uint8_t cs;
  uint8_t idx = 0;
  uint8_t i;

  if (data_len > QI_FRAME_MAX_DATA_LEN)
  {
    return -1;
  }

  /* LEN = CMD(1) + DATA(n) */
  len_field = (uint8_t)(1U + data_len);

  /* 组装帧 */
  buf[idx++] = QI_FRAME_HEADER1;  /* 0x55 */
  buf[idx++] = QI_FRAME_HEADER2;  /* 0xAA */
  buf[idx++] = len_field;          /* LEN */
  buf[idx++] = cmd;                /* CMD */
  for (i = 0U; i < data_len; i++)  /* DATA */
  {
    buf[idx++] = data[i];
  }
  buf[idx++] = seq;                /* SEQ */

  /* 计算校验和：头 + 长度 + 命令 + 数据 + 流水号，取低 8 位 */
  cs = 0;
  for (i = 0U; i < idx; i++)
  {
    cs += buf[i];
  }
  buf[idx++] = cs & 0xFFU;         /* CS */

  qi_uart_send(buf, idx);
  return 0;
}

/**
 * @brief  发送 ACK 应答
 * @param  status: 应答状态（0x00=成功）
 * @param  seq: 流水号（与请求帧一致）
 */
void qi_protocol_send_ack(uint8_t status, uint8_t seq)
{
  uint8_t data[1];
  data[0] = status;
  qi_protocol_send(QI_CMD_ACK, data, 1U, seq);
}

/**
 * @brief  发送功率设置命令
 * @param  power: 功率值（QI_POWER_5W/10W/15W）
 * @param  seq: 流水号
 * @retval 0=成功，-1=参数错误
 */
int8_t qi_protocol_set_power(uint8_t power, uint8_t seq)
{
  uint8_t data[1];
  data[0] = power;
  return qi_protocol_send(QI_CMD_SET_POWER, data, 1U, seq);
}

/**
 * @brief  构建并发送一帧 IAP 数据 (Command 0xCC)
 * @note   IAP 帧格式：[0x55][0xAA][LEN][CMD][DATA...][CS]，不含流水号
 *         LEN = CMD(1) + DATA(n), CS 不含 SEQ
 * @param  data: 帧数据指针（不可为 NULL，至少1字节子命令）
 * @param  data_len: 帧数据长度
 * @retval 0=成功，-1=参数错误
 */
int8_t qi_protocol_send_iap(const uint8_t *data, uint8_t data_len)
{
  uint8_t buf[QI_FRAME_MIN_LEN_IAP + QI_FRAME_MAX_DATA_LEN];
  uint8_t len_field;
  uint8_t cs;
  uint8_t idx = 0;
  uint8_t i;

  if ((data == (const uint8_t *)0) || (data_len == 0U) ||
      (data_len > QI_FRAME_MAX_DATA_LEN))
  {
    return -1;
  }

  /* LEN = CMD(1) + DATA(n), 不含 SEQ */
  len_field = (uint8_t)(1U + data_len);

  /* 组装帧 */
  buf[idx++] = QI_FRAME_HEADER1;  /* 0x55 */
  buf[idx++] = QI_FRAME_HEADER2;  /* 0xAA */
  buf[idx++] = len_field;          /* LEN */
  buf[idx++] = QI_CMD_IAP;        /* CMD = 0xCC */
  for (i = 0U; i < data_len; i++)  /* DATA */
  {
    buf[idx++] = data[i];
  }

  /* 计算校验和：头 + 长度 + 命令 + 数据，取低 8 位 (不含 SEQ) */
  cs = 0;
  for (i = 0U; i < idx; i++)
  {
    cs += buf[i];
  }
  buf[idx++] = cs & 0xFFU;         /* CS */

  qi_uart_send(buf, idx);
  return 0;
}

int8_t qi_protocol_iap_prepare(uint16_t fw_size)
{
  uint8_t d[3];

  d[0] = QI_IAP_PREPARE;
  d[1] = (uint8_t)((fw_size >> 8) & 0xFFU);
  d[2] = (uint8_t)(fw_size & 0xFFU);
  return qi_protocol_send_iap(d, 3U);
}

int8_t qi_protocol_iap_data(uint16_t addr, const uint8_t *payload, uint8_t payload_len)
{
  uint8_t d[3U + QI_IAP_MAX_CHUNK];
  uint8_t i;

  if ((payload == (const uint8_t *)0) || (payload_len == 0U))
  {
    return -1;
  }
  if (payload_len > QI_IAP_MAX_CHUNK)
  {
    payload_len = QI_IAP_MAX_CHUNK;
  }
  d[0] = QI_IAP_DATA;
  d[1] = (uint8_t)((addr >> 8) & 0xFFU);
  d[2] = (uint8_t)(addr & 0xFFU);
  for (i = 0U; i < payload_len; i++)
  {
    d[3U + i] = payload[i];
  }
  return qi_protocol_send_iap(d, (uint8_t)(3U + payload_len));
}

/**
 * @brief  主循环轮询
 * @note   从 UART 缓冲区读取字节并送入协议状态机解析
 *         应在 main loop 中周期调用
 */
void qi_protocol_poll(void)
{
  uint8_t byte;

  while (qi_uart_rx_available() > 0U)
  {
    if (qi_uart_rx_read(&byte) == 0)
    {
      rx_process_byte(byte);
    }
  }
}
