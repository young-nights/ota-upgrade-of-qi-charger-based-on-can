/**
  **************************************************************************
  * @file     qi_protocol.h
  * @brief    Qi 无线充电芯片 UART 通信协议层
  *
  * @details  基于 qi_uart.c 底层驱动，实现 Qi 芯片的协议解析和命令收发。
  *           协议格式：帧头(2B) + 长度(1B) + 命令(1B) + 数据(nB) + 流水号(1B) + 校验(1B)
  *           通信接口：USART2 (PA2=TX, PA3=RX)，9600 8N1
  *
  *           命令码：
  *           0x00 - Qi→MCU 回复指令 (ACK)
  *           0x01 - Qi→MCU 无线充上报数据（充电状态）
  *           0x02 - MCU→Qi 设置功率
  *           0xCC - MCU→Qi IAP 流程（固件升级）
  **************************************************************************
  */

#ifndef __QI_PROTOCOL_H
#define __QI_PROTOCOL_H

#ifdef __cplusplus
extern "C" {
#endif

#include "at32f422_426.h"

/* ==========================================================================
 *  帧格式常量
 * ========================================================================== */

#define QI_FRAME_HEADER1        0x55U   /**< 帧起始头第1字节 */
#define QI_FRAME_HEADER2        0xAAU   /**< 帧起始头第2字节 */
#define QI_FRAME_HEADER_LEN     2U      /**< 帧头长度 */
#define QI_FRAME_MIN_LEN        8U      /**< 最小帧长度：头(2)+长度(1)+命令(1)+数据(0)+流水号(1)+校验(1) */
#define QI_FRAME_MIN_LEN_IAP    6U      /**< IAP 帧最小长度：头(2)+长度(1)+命令(1)+数据(0)+校验(1)，无流水号 */
#define QI_FRAME_MAX_DATA_LEN   32U     /**< 帧数据最大长度 */

/* ==========================================================================
 *  命令码
 * ========================================================================== */

#define QI_CMD_ACK              0x00U   /**< Qi→MCU 回复指令 */
#define QI_CMD_STATUS_REPORT    0x01U   /**< Qi→MCU 无线充上报数据 */
#define QI_CMD_SET_POWER        0x02U   /**< MCU→Qi 设置功率 */
#define QI_CMD_IAP              0xCCU   /**< MCU→Qi IAP 固件升级 */

/* ==========================================================================
 *  IAP 子命令
 * ========================================================================== */

#define QI_IAP_PREPARE          0x01U   /**< 准备升级（含固件大小） */
#define QI_IAP_DATA             0x02U   /**< 发送固件数据（含地址+数据） */

/* ==========================================================================
 *  ACK 状态码
 * ========================================================================== */

#define QI_ACK_OK               0x00U   /**< 接收成功 */

/* ==========================================================================
 *  设置功率值
 * ========================================================================== */

#define QI_POWER_5W             0x01U   /**< 5W */
#define QI_POWER_10W            0x02U   /**< 10W */
#define QI_POWER_15W            0x03U   /**< 15W */

/* ==========================================================================
 *  状态字节1 位定义（上报数据偏移 4）
 * ========================================================================== */

#define QI_STATUS_PING          (1U << 0)  /**< b0: 1=检测到设备放置 */
#define QI_STATUS_CHARGING      (1U << 1)  /**< b1: 1=正在充电 */
#define QI_STATUS_FULL          (1U << 2)  /**< b2: 1=电池已充满 */
#define QI_STATUS_OVP           (1U << 3)  /**< b3: 1=过压保护 */
#define QI_STATUS_UVP           (1U << 4)  /**< b4: 1=欠压保护 */
#define QI_STATUS_OCP           (1U << 5)  /**< b5: 1=过流保护 */
#define QI_STATUS_OTP           (1U << 6)  /**< b6: 1=过温保护 */
#define QI_STATUS_FOD           (1U << 7)  /**< b7: 1=异物检测保护 */

/* ==========================================================================
 *  接收状态机
 * ========================================================================== */

/** @brief 接收状态机状态 */
typedef enum {
  QI_RX_STATE_IDLE = 0,   /**< 等待帧头第1字节 0x55 */
  QI_RX_STATE_HEADER2,    /**< 等待帧头第2字节 0xAA */
  QI_RX_STATE_LENGTH,     /**< 等待帧长度 */
  QI_RX_STATE_CMD,        /**< 等待命令码 */
  QI_RX_STATE_DATA,       /**< 接收帧数据 */
  QI_RX_STATE_SEQ,        /**< 等待流水号 */
  QI_RX_STATE_CS          /**< 等待校验和 */
} qi_rx_state_t;

/** @brief 解析后的帧结构 */
typedef struct {
  uint8_t cmd;                           /**< 命令码 */
  uint8_t data[QI_FRAME_MAX_DATA_LEN];  /**< 帧数据 */
  uint8_t data_len;                      /**< 帧数据长度 */
  uint8_t seq;                           /**< 流水号 (IAP 帧此字段无效) */
  uint8_t expect_seq;                    /**< 1=普通帧含流水号, 0=IAP 帧不含 */
  uint8_t cs;                            /**< 接收到的校验和 */
} qi_frame_t;

/** @brief 帧接收回调函数类型 */
typedef void (*qi_frame_callback_t)(const qi_frame_t *frame);

/* ==========================================================================
 *  导出函数
 * ========================================================================== */

/**
 * @brief  初始化 Qi 协议层
 * @note   调用 qi_uart_init() 初始化底层 UART，注册回调
 */
void qi_protocol_init(void);

/**
 * @brief  注册帧接收回调
 * @param  cb: 回调函数指针，收到完整帧时调用
 */
void qi_protocol_register_callback(qi_frame_callback_t cb);

/**
 * @brief  构建并发送一帧数据
 * @param  cmd: 命令码
 * @param  data: 帧数据指针（可为 NULL）
 * @param  data_len: 帧数据长度
 * @param  seq: 流水号
 * @retval 0=成功，-1=参数错误
 */
int8_t qi_protocol_send(uint8_t cmd, const uint8_t *data, uint8_t data_len, uint8_t seq);

/**
 * @brief  构建并发送一帧 IAP 数据 (Command 0xCC)
 * @note   IAP 帧格式：[0x55][0xAA][LEN][CMD][DATA...][CS]，不含流水号
 *         LEN = CMD(1) + DATA(n)
 * @param  data: 帧数据指针（不可为 NULL，至少1字节子命令）
 * @param  data_len: 帧数据长度
 * @retval 0=成功，-1=参数错误
 */
int8_t qi_protocol_send_iap(const uint8_t *data, uint8_t data_len);

/**
 * @brief  发送 ACK 应答
 * @param  status: 应答状态（0x00=成功）
 * @param  seq: 流水号（与请求帧一致）
 */
void qi_protocol_send_ack(uint8_t status, uint8_t seq);

/**
 * @brief  发送功率设置命令
 * @param  power: 功率值（QI_POWER_5W/10W/15W）
 * @param  seq: 流水号
 * @retval 0=成功，-1=参数错误
 */
int8_t qi_protocol_set_power(uint8_t power, uint8_t seq);

/**
 * @brief  主循环轮询（处理接收到的字节并解析帧）
 * @note   应在 main loop 中周期调用
 */
void qi_protocol_poll(void);

#ifdef __cplusplus
}
#endif

#endif /* __QI_PROTOCOL_H */
