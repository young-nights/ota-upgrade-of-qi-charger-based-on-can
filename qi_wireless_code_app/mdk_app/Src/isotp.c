/**
  **************************************************************************
  * @file     isotp.c
  * @brief    ISO 15765-2 (ISO-TP) transport layer for CAN UDS communication
  **************************************************************************
  */

#include "isotp.h"
#include "can_driver.h"
#include "timer_drv.h"
#include "at32f422_426.h"
#include <string.h>

/* FC 送达确认与重试参数（FC 发送竞态修复 + 残余竞态加固）：
 * 总尝试 ≤5 次，尝试间 5ms tick 间隔；单帧上总线确认窗口 50ms——
 * FC 只要入队就有 handle，等的是上总线；50ms 内 CCU 的 1000ms N_As
 * 仍有充足余量。发 FC 前等上一帧落地窗口取 5ms。
 * 均为本文件内部参数，不动 isotp.h 导出面。 */
#define ISOTP_FC_ATTEMPTS_MAX           5U
#define ISOTP_FC_ATTEMPT_GAP_MS         5U
#define ISOTP_FC_CONFIRM_TIMEOUT_MS     50U
#define ISOTP_FC_PREWAIT_TIMEOUT_MS     5U

static isotp_rx_ctx_t rx_ctx;
static isotp_complete_cb_t complete_callback = (isotp_complete_cb_t)0;

static volatile uint8_t g_fc_flag = 0;
static uint8_t g_fc_fs = 0;
static uint8_t g_fc_bs = 0;
static uint8_t g_fc_stmin = 0;

/* FC 延迟发送：FF 在 ISR 上下文到达时只置标志，FC 由 isotp_poll()
 * 在主循环发出。Flash 擦除（__disable_irq）后 CAN 常处 bus-off，
 * ISR 内阻塞重试 FC 必然全失败；主循环上下文可做 bus-off 恢复。 */
static volatile uint8_t g_fc_pending = 0;
static uint32_t g_fc_pending_can_id = 0;
static uint8_t g_fc_pending_status = 0;

/* TX payload 副本：isotp_wait_cts 期间处理新 UDS 请求时，
 * proto_send_response 可能覆写 g_tx_pend；MF 发送从此副本读取。 */
static uint8_t g_tx_copy[ISOTP_MAX_PAYLOAD];

static void isotp_delay_ms(uint32_t ms);

/**
 * @brief  发送一帧 CAN（带重试）
 * @param  can_id:   CAN ID
 * @param  frame:    8B 帧数据
 * @param  len:      DLC
 * @param  fast:     1=快速模式（CF 连发）：跳过 pre-wait 和确认等待，
 *                   STmin 已保证帧间隔，无需等 TX idle
 *                   0=完整模式（FF/FC）：pre-wait + 确认，防 SN 乱序
 * @retval 0=成功，-1=失败
 */
static int8_t isotp_can_send(uint32_t can_id, uint8_t *frame, uint8_t len,
                             uint8_t fast)
{
  uint8_t h_before;
  uint8_t h_after;
  uint8_t have_prev;
  uint32_t start;

  if (fast == 0U)
  {
    /* Full mode (FF/FC): pre-wait + confirm. */
    (void)can_driver_wait_tx_idle(50U);
    start = timer_get_tick();
    while ((timer_get_tick() - start) < ISOTP_N_AS_TIMEOUT_MS)
    {
      have_prev = (can_driver_last_tx_handle(&h_before) == 0) ? 1U : 0U;
      if (can_driver_send(can_id, frame, len) == 0)
      {
        if ((can_driver_last_tx_handle(&h_after) == 0) &&
            ((have_prev == 0U) || (h_after != h_before)))
        {
          /* 确认窗口 50ms 即可（帧上总线 ~0.5ms）；1000ms 会在
           * handle 确认偶尔丢失时把主循环卡死，拖垮后续 FC/应答。 */
          (void)can_driver_wait_tx_frame(h_after, 50U);
        }
        return 0;
      }
    }
  }
  else
  {
    /* Fast mode (CF/SF): pre-wait + retry, no confirm. */
    (void)can_driver_wait_tx_idle(50U);
    start = timer_get_tick();
    while ((timer_get_tick() - start) < 500U)
    {
      if (can_driver_send(can_id, frame, len) == 0)
      {
        return 0;
      }
      isotp_delay_ms(1U);
    }
  }
  return -1;
}

static void isotp_delay_ms(uint32_t ms)
{
  uint32_t start = timer_get_tick();
  if (ms == 0U)
  {
    return;
  }
  while ((timer_get_tick() - start) < ms)
  {
  }
}

/**
 * @brief  single FC transmit attempt: enqueue + confirm the frame on bus
 * @note   单次尝试快速出结论，不走 isotp_can_send 的 N_As=1s 自旋（FC
 *         时效以毫秒计，自旋会占死 RX 调用上下文）。发送结果不丢弃：
 *         发送前后 last_tx_handle 对比识别静默入队失败（同 ota_download.c
 *         0x37 收尾写法），入队成功后按 handle 确认该帧真正上总线。
 * @param  can_id:  FC frame CAN ID
 * @param  fc_data: filled 8-byte FC frame
 * @retval  0 = FC confirmed on bus
 *          1 = FC enqueued but confirm timed out（帧已入队，可能仍在
 *              发送中；调用方不应清 RX 状态——CCU 的 CF 仍需被接收）
 *         -1 = never enqueued 或 enqueued but ABORTED/REJECTED（硬件
 *              终态失败，帧不会再上总线，调用方应重试）
 */
static int8_t isotp_send_fc_try(uint32_t can_id, uint8_t *fc_data)
{
  uint8_t h_before;
  uint8_t h_after;
  uint8_t have_prev;
  int8_t  wait_rc;

  /* Pre-wait for TX idle: the previous UDS response (e.g. 27 01 seed
   * multi-frame) may still be transmitting when the next request's FF
   * arrives. Without this, can_driver_send rejects both mailboxes and
   * FC never reaches the host (27 02 66B / 0x34 11B multi-frame timeout). */
  (void)can_driver_wait_tx_idle(50U);

  have_prev = (can_driver_last_tx_handle(&h_before) == 0) ? 1U : 0U;

  if (can_driver_send(can_id, fc_data, ISOTP_CAN_FRAME_SIZE) != 0)
  {
    return -1;
  }

  /* can_driver_send 成功 = 帧已入队。handle 未推进不能当作未入队
   * 否则 5 次“失败”后 isotp_send_fc 会清 rx_ctx.state，主机收到 FC
   * 发来的 CF 被丢弃（0x34 多帧无响应的根因之一）。 */
  if ((can_driver_last_tx_handle(&h_after) != 0) ||
      ((have_prev != 0U) && (h_after == h_before)))
  {
    return 1;
  }

  wait_rc = can_driver_wait_tx_frame(h_after, ISOTP_FC_CONFIRM_TIMEOUT_MS);
  if (wait_rc == 0)
  {
    return 0;
  }
  /* 确认超时/终态失败都算已入队（帧可能已在总线上），返回 1 不重试。 */
  return 1;
}

/**
 * @brief  send one Flow Control frame with delivery confirmation + retry
 * @note   FC 发送竞态修复 + 残余竞态加固：
 *         1) 发 FC 前先等上一帧落地：FF 常在上一应答（如擦除应答
 *            71 01 FF 00）发出后 1~2ms 内到达，两帧挤入同一写锁未稳定
 *            窗口会放大丢失概率。上一帧已落地时 wait_tx_frame 正证据
 *            立即返回不增时延；确认超时不停摆，照发 FC。
 *         2) 发送结果不丢弃 + 自动重试：单次尝试见 isotp_send_fc_try，
 *            失败按 ISOTP_FC_ATTEMPT_GAP_MS tick 间隔重发，总尝试 ≤
 *            ISOTP_FC_ATTEMPTS_MAX 次。重试产生重复 FC 对 ISO-TP 安全
 *            （接收方幂等处理）。
 *         3) 全部 can_driver_send 失败也不清 rx_ctx.state：FF 分支
 *            在调用本函数前已置 RX_IN_PROGRESS，这里清状态会把随后
 *            到达的 CF 丢掉。超时清理交给 isotp_poll 的 N_Cr。
 *         FC 无 suppress 语义，CTS/OVERFLOW 任何状态走同一送达确认。
 * @param  can_id: FC frame CAN ID
 * @param  status: FC status (CTS/WAIT/OVERFLOW, ISOTP_FC_STATUS_*)
 * @param  bs:     Block Size
 * @param  stmin:  Separation Time
 * @retval none
 */
static void isotp_send_fc(uint32_t can_id, uint8_t status, uint8_t bs, uint8_t stmin)
{
  uint8_t fc_data[ISOTP_CAN_FRAME_SIZE];
  uint8_t prev_handle;
  uint8_t attempt;
  uint8_t i;

  fc_data[0] = ISOTP_PCI_TYPE_FC | (status & 0x0FU);
  fc_data[1] = bs;
  fc_data[2] = stmin;
  for (i = 3U; i < ISOTP_CAN_FRAME_SIZE; i++)
  {
    fc_data[i] = 0xCCU;
  }

  /* Flash 擦除禁中断后 CAN 可能处于 bus-off 或更深错误态。
   * can_busoff_reset 仅请求恢复（需 5.6ms 隐性位），不够可靠；
   * offline→online 完整重初始化：复位 CAN 外设、重挂中断、
   * 重配时序/滤波、清错误计数器。 */
  if (can_busoff_get(CAN1) != RESET)
  {
    can_driver_offline();
    can_driver_online();
  }

  if (can_driver_last_tx_handle(&prev_handle) == 0)
  {
    (void)can_driver_wait_tx_frame(prev_handle, ISOTP_FC_PREWAIT_TIMEOUT_MS);
  }

  for (attempt = 0U; attempt < ISOTP_FC_ATTEMPTS_MAX; attempt++)
  {
    if (attempt > 0U)
    {
      isotp_delay_ms(ISOTP_FC_ATTEMPT_GAP_MS);
    }
    /* 重试间再查 bus-off：首次恢复可能未完成 */
    if ((attempt > 0U) && (can_busoff_get(CAN1) != RESET))
    {
      can_driver_offline();
      can_driver_online();
    }
    {
      int8_t rc = isotp_send_fc_try(can_id, fc_data);
      if (rc == 0)
      {
        /* N_Cr 从 FC 上总线起算：主机收到 FC 后才会发 CF。 */
        rx_ctx.last_cf_ms = timer_get_tick();
        return;
      }
      if (rc > 0)
      {
        /* FC 已入队（确认超时但帧大概率已上总线），不重试不清洗 */
        rx_ctx.last_cf_ms = timer_get_tick();
        return;
      }
    }
  }

  /* 全部尝试 can_driver_send 都失败：绝不 rx_ctx.state=IDLE。
   * FF 分支已置 RX_IN_PROGRESS，清状态会丢掉随后到达的 CF。 */
}

static uint8_t isotp_stmin_to_ms(uint8_t stmin)
{
  if (stmin <= 0x7FU)
  {
    return (stmin == 0U) ? 0U : stmin;
  }
  return 1U;
}

void isotp_init(isotp_complete_cb_t cb)
{
  rx_ctx.state        = ISOTP_RX_STATE_IDLE;
  rx_ctx.total_len    = 0;
  rx_ctx.received_len = 0;
  rx_ctx.expected_sn  = 0;
  rx_ctx.last_cf_ms   = 0;
  complete_callback   = cb;
  g_fc_flag           = 0;
  g_fc_pending        = 0;
}

void isotp_poll(void)
{
  /* FC 延迟发送：主循环上下文处理，可做 bus-off 恢复。
   * FF 在 ISR 到达时只置 g_fc_pending，FC 在此发出。 */
  if (g_fc_pending != 0U)
  {
    g_fc_pending = 0;
    isotp_send_fc(g_fc_pending_can_id, g_fc_pending_status,
                  ISOTP_FC_DEFAULT_BS, ISOTP_FC_DEFAULT_STMIN);
  }

  if (rx_ctx.state != ISOTP_RX_STATE_RX_IN_PROGRESS)
  {
    return;
  }
  if ((timer_get_tick() - rx_ctx.last_cf_ms) >= ISOTP_N_CR_TIMEOUT_MS)
  {
    rx_ctx.state = ISOTP_RX_STATE_IDLE;
  }
}

uint8_t isotp_rx_busy(void)
{
  return (rx_ctx.state == ISOTP_RX_STATE_RX_IN_PROGRESS) ? 1U : 0U;
}

void isotp_rx_process(uint8_t *data, uint8_t len)
{
  uint8_t pci_type;
  uint16_t payload_len;
  uint16_t copy_len;
  uint16_t i;

  if ((data == (uint8_t *)0) || (len == 0U))
  {
    return;
  }

  pci_type = data[0] & ISOTP_PCI_TYPE_MASK;

  switch (pci_type)
  {
    case ISOTP_PCI_TYPE_SF:
    {
      uint8_t sf_dl = data[0] & 0x0FU;
      if ((sf_dl == 0U) || (sf_dl > (uint8_t)(len - 1U)))
      {
        return;
      }
      rx_ctx.state = ISOTP_RX_STATE_IDLE;
      if (complete_callback != (isotp_complete_cb_t)0)
      {
        complete_callback(&data[1], (uint16_t)sf_dl);
      }
      break;
    }

    case ISOTP_PCI_TYPE_FF:
    {
      if (len < 2U)
      {
        return;
      }

      payload_len = ((uint16_t)(data[0] & 0x0FU) << 8) | (uint16_t)data[1];
      if (payload_len <= 7U)
      {
        return;
      }
      if (payload_len > ISOTP_MAX_PAYLOAD)
      {
        /* OVERFLOW FC 也延迟到主循环发送 */
        g_fc_pending_can_id = CAN_ID_UDS_RESPONSE;
        g_fc_pending_status = ISOTP_FC_STATUS_OVERFLOW;
        g_fc_pending = 1;
        return;
      }

      rx_ctx.total_len    = payload_len;
      rx_ctx.received_len = 0;
      rx_ctx.expected_sn  = 1;
      rx_ctx.state        = ISOTP_RX_STATE_RX_IN_PROGRESS;
      rx_ctx.last_cf_ms   = timer_get_tick();

      if (len > 2U)
      {
        copy_len = (uint16_t)(len - 2U);
        if (copy_len > payload_len)
        {
          copy_len = payload_len;
        }
        for (i = 0; i < copy_len; i++)
        {
          rx_ctx.buf[i] = data[2 + i];
        }
        rx_ctx.received_len = copy_len;
      }

      /* 立即发 FC（主循环上下文，非 ISR）。不用 isotp_send_fc 的
       * bus-off 恢复/重试路径：offline→online 会清 RX FIFO，若主机
       * 已按上一次 FC 发来 CF，会在这里被抹掉。 */
      {
        uint8_t fc[8];
        uint8_t fi;
        fc[0] = ISOTP_PCI_TYPE_FC | ISOTP_FC_STATUS_CTS;
        fc[1] = ISOTP_FC_DEFAULT_BS;
        fc[2] = ISOTP_FC_DEFAULT_STMIN;
        for (fi = 3U; fi < 8U; fi++)
        {
          fc[fi] = 0xCCU;
        }
        (void)can_driver_send(CAN_ID_UDS_RESPONSE, fc, 8U);
      }
      rx_ctx.last_cf_ms = timer_get_tick();
      g_fc_pending = 0;
      break;
    }

    case ISOTP_PCI_TYPE_CF:
    {
      if (rx_ctx.state != ISOTP_RX_STATE_RX_IN_PROGRESS)
      {
        return;
      }

      {
        uint8_t sn = data[0] & 0x0FU;
        if (sn != rx_ctx.expected_sn)
        {
          /* 忽略乱序/重复 CF，绝不 abort：主机重发 CF 或总线重复帧
           * 会把整包组包打死（0x34 无响应）。超时清理交给 N_Cr。 */
          return;
        }
      }

      rx_ctx.last_cf_ms = timer_get_tick();
      copy_len = (uint16_t)(len - 1U);
      if (copy_len > (rx_ctx.total_len - rx_ctx.received_len))
      {
        copy_len = rx_ctx.total_len - rx_ctx.received_len;
      }
      for (i = 0; i < copy_len; i++)
      {
        rx_ctx.buf[rx_ctx.received_len + i] = data[1 + i];
      }
      rx_ctx.received_len += copy_len;
      rx_ctx.expected_sn = (rx_ctx.expected_sn + 1U) & 0x0FU;

      if (rx_ctx.received_len >= rx_ctx.total_len)
      {
        rx_ctx.state = ISOTP_RX_STATE_IDLE;
        if (complete_callback != (isotp_complete_cb_t)0)
        {
          complete_callback(rx_ctx.buf, rx_ctx.total_len);
        }
      }
      break;
    }

    case ISOTP_PCI_TYPE_FC:
    {
      g_fc_fs = data[0] & 0x0FU;
      g_fc_bs = (len > 1U) ? data[1] : 0U;
      g_fc_stmin = (len > 2U) ? data[2] : 0U;
      g_fc_flag = 1;
      break;
    }

    default:
      break;
  }
}

static int8_t isotp_wait_cts(uint8_t *out_bs, uint8_t *out_stmin)
{
  uint32_t start = timer_get_tick();
  uint32_t id;
  uint8_t data[ISOTP_CAN_FRAME_SIZE];
  uint8_t n;

  g_fc_flag = 0;

  while ((timer_get_tick() - start) < ISOTP_N_BS_TIMEOUT_MS)
  {
    if (g_fc_flag != 0U)
    {
      g_fc_flag = 0;
      if (g_fc_fs == ISOTP_FC_STATUS_CTS)
      {
        *out_bs = g_fc_bs;
        *out_stmin = g_fc_stmin;
        return 0;
      }
      if (g_fc_fs == ISOTP_FC_STATUS_OVERFLOW)
      {
        return -1;
      }
      start = timer_get_tick();
      continue;
    }

    if (can_driver_recv(&id, data, &n) != 0)
    {
      continue;
    }
    /* Ignore non-UDS IDs (lifecycle, own TX echo). A raw SF PCI 0x02
     * otherwise becomes UDS SID 0x02 → spurious 7F 02 11. */
    if ((id != CAN_ID_UDS_REQUEST) &&
        ((id & 0x1FFFFF00U) != 0x18DB3300U))
    {
      continue;
    }
    if ((n == 0U) || ((data[0] & ISOTP_PCI_TYPE_MASK) != ISOTP_PCI_TYPE_FC))
    {
      /* Non-FC frame: process IMMEDIATELY (was: stash for later).
       * Stashing delayed UDS handling until isotp_tx_send completed,
       * causing host timeout on rapid successive requests (SecurityAccess
       * retry, 22 F1 95 after wake). Processing inline lets the UDS
       * handler generate a response — SF responses are sent inline (safe,
       * isotp_tx_send has its own payload copy); MF responses queue in
       * g_tx_pend and are flushed on the next can_protocol_poll. */
      isotp_rx_process(data, n);
      continue;
    }
    isotp_rx_process(data, n);
  }
  return -1;
}

int8_t isotp_tx_send(uint32_t can_id, uint8_t *payload, uint16_t len)
{
  uint8_t frame[ISOTP_CAN_FRAME_SIZE];
  uint16_t offset;
  uint8_t sn;
  uint8_t copy_len;
  uint8_t bs;
  uint8_t stmin;
  uint8_t cf_in_block;

  if ((payload == (uint8_t *)0) || (len == 0U) || (len > ISOTP_MAX_PAYLOAD))
  {
    return -1;
  }

  if (len <= 7U)
  {
    uint16_t i;
    frame[0] = ISOTP_PCI_TYPE_SF | (uint8_t)len;
    for (i = 0; i < len; i++)
    {
      frame[1 + i] = payload[i];
    }
    for (i = 1U + len; i < ISOTP_CAN_FRAME_SIZE; i++)
    {
      frame[i] = 0xCCU;
    }
    return isotp_can_send(can_id, frame, ISOTP_CAN_FRAME_SIZE, 1U);
  }

  /* MF only: copy payload to internal buffer. isotp_wait_cts processes
   * incoming UDS requests inline; a nested SF isotp_tx_send must NOT
   * overwrite this buffer while the MF transfer is still reading from it. */
  memcpy(g_tx_copy, payload, len);
  payload = g_tx_copy;

  frame[0] = ISOTP_PCI_TYPE_FF | (uint8_t)((len >> 8) & 0x0FU);
  frame[1] = (uint8_t)(len & 0xFFU);
  {
    uint8_t i;
    for (i = 0; i < 6U; i++)
    {
      frame[2 + i] = payload[i];
    }
  }
  if (isotp_can_send(can_id, frame, ISOTP_CAN_FRAME_SIZE, 0U) != 0)
  {
    return -1;
  }

  bs = 0;
  stmin = ISOTP_FC_DEFAULT_STMIN;
  if (isotp_wait_cts(&bs, &stmin) != 0)
  {
    return -1;
  }

  offset = 6U;
  sn = 1U;
  cf_in_block = 0;

  while (offset < len)
  {
    uint8_t i;

    if ((bs != 0U) && (cf_in_block >= bs))
    {
      if (isotp_wait_cts(&bs, &stmin) != 0)
      {
        return -1;
      }
      cf_in_block = 0;
    }

    {
      uint32_t gap = isotp_stmin_to_ms(stmin);
      if (gap < 1U)
      {
        gap = 1U;
      }
      isotp_delay_ms(gap);
    }

    frame[0] = ISOTP_PCI_TYPE_CF | (sn & 0x0FU);
    copy_len = (uint8_t)(len - offset);
    if (copy_len > 7U)
    {
      copy_len = 7U;
    }
    for (i = 0; i < copy_len; i++)
    {
      frame[1 + i] = payload[offset + i];
    }
    for (i = 1U + copy_len; i < ISOTP_CAN_FRAME_SIZE; i++)
    {
      frame[i] = 0xCCU;
    }
    if (isotp_can_send(can_id, frame, ISOTP_CAN_FRAME_SIZE, 1U) != 0)
    {
      return -1;
    }

    offset += copy_len;
    sn = (sn + 1U) & 0x0FU;
    cf_in_block++;
  }

  return 0;
}
