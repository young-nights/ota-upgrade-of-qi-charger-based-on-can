/**
  **************************************************************************
  * @file     can_protocol.c
  * @brief    CAN UDS protocol handler for APP firmware
  **************************************************************************
  *
  * Copyright (c) 2025, Artery Technology, All rights reserved.
  *
  * The software Board Support Package (BSP) that is made available to
  * download from Artery official website is the copyrighted work of Artery.
  * Artery authorizes customers to use, copy, and distribute the BSP
  * software and its related documentation for the purpose of design and
  * development in conjunction with Artery microcontrollers. Use of the
  * software is governed by this copyright notice and the following disclaimer.
  *
  * THIS SOFTWARE IS PROVIDED ON "AS IS" BASIS WITHOUT WARRANTIES,
  * GUARANTEES OR REPRESENTATIONS OF ANY KIND. ARTERY EXPRESSLY DISCLAIMS,
  * TO THE FULLEST EXTENT PERMITTED BY LAW, ALL EXPRESS, IMPLIED OR
  * STATUTORY OR OTHER WARRANTIES, GUARANTEES OR REPRESENTATIONS,
  * INCLUDING BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY,
  * FITNESS FOR A PARTICULAR PURPOSE, OR NON-INFRINGEMENT.
  *
  **************************************************************************
  */

/* includes ------------------------------------------------------------------*/
#include "can_protocol.h"
#include "can_driver.h"
#include "ota_trigger.h"
#include "ota_download.h"
#include "isotp.h"
#include "timer_drv.h"
#include "lifecycle.h"
#include "device_info.h"
#include "board_gpio.h"
#include "qi_protocol.h"
#include "qi_uart.h"
#include "qi_uart_sniff.h"
#include "nvm_drv.h"
#include "sha256.h"
#include "uECC.h"
#include "sit1145.h"
#include "at32f422_426_can.h"
#include <string.h>

/* ========================================================================== */
/*  Version string constants (UTF-8, max 16 bytes including null terminator)  */
/*                                                                            */
/*  SW_VERSION_STR 是运行软件版本的【唯一真相源】：UDS DID 0xF195 应答直接  */
/*  取此 APP 编译时常量，不读 OTA metadata / XATO 镜像头（2026-09-18 起：   */
/*  镜像头 version 区打包固定 0x00，不携带版本号；metadata 无软件版本字段）。 */
/*  改版本号只改本常量 + docs 文档，无打包脚本联动环节。                     */
/* ========================================================================== */

/* 跳版本说明：1.1.9/1.1.10 的版本串已被 TC-0508 十个测试镜像占用，且打包   */
/* 输出名 app_image_vX_Y_Z.bin 会与既有测试镜像文件名冲突，故 1.1.8→1.1.11  */
static const char SW_VERSION_STR[]     = "QC_JYF_FW_1.1.13";  /*!< 运行版本唯一真相源 */
static const char BOOTLOADER_VER_STR[] = "QC_JYF_BL_1.0.0";
static const char HW_VERSION_STR[]     = "QC_JYF_HW_1.1.5";

/* same public key as Bootloader boot_verify.c */
const uint8_t g_app_ecdsa_pubkey[65] = {
  0x04,
  0x79, 0x0d, 0x96, 0xca, 0x91, 0x2d, 0x90, 0xdb,
  0x73, 0xdf, 0x21, 0xb0, 0x6e, 0xe7, 0xce, 0x19,
  0xaa, 0x7c, 0x1f, 0x75, 0x30, 0x55, 0x0a, 0x48,
  0x21, 0x84, 0x19, 0xb4, 0x4b, 0x4c, 0x37, 0xcb,
  0xf5, 0x7c, 0xd3, 0xfc, 0x9e, 0x26, 0xbe, 0x1b,
  0xa6, 0x94, 0xdd, 0x45, 0x62, 0x7e, 0xaa, 0xca,
  0x71, 0x38, 0xf5, 0x7a, 0x8e, 0xa8, 0xd5, 0xdd,
  0x20, 0x70, 0x33, 0x26, 0xf0, 0x95, 0x41, 0x71
};

#define SECURITY_LOCKOUT_MS    30000U
#define SECURITY_MAX_FAILURES  3U

static void session_reset_to_default(void);

static uint8_t  current_session           = SESSION_DEFAULT;
static uint8_t  security_unlocked         = 0;
static uint32_t last_tester_present_tick  = 0;
static uint8_t  g_seed_generated          = 0;
static uint8_t  g_seed[32];
static uint8_t  g_seed_sub                = 0;
static uint8_t  g_security_fail_count     = 0;
static uint32_t g_security_lockout_until_ms = 0;
static uint8_t  g_sa_sig_buf[64];
static uint8_t  g_sa_sig_bytes_received   = 0;
static uint8_t  g_sa_sig_block_seq        = 0;

/* Qi IAP state tracking */
#define QI_IAP_IDLE         0x00U
#define QI_IAP_IN_PROGRESS  0x01U
#define QI_IAP_SUCCESS      0x02U
#define QI_IAP_FAILED       0x03U
#define QI_IAP_WAIT_ACK     0x04U   /*!< waiting for Qi chip UART ACK */

/* Qi IAP auto-complete timeout after last data packet sent */
#define QI_IAP_DONE_TIMEOUT_MS  3000U

/** @brief  Qi chip UART ACK timeout (ms) for each data packet (flash write) */
#define QI_IAP_ACK_TIMEOUT_MS      2000U
/** @brief  Qi chip prepare/erase timeout (ms) for DID 0x2130 start */
#define QI_IAP_PREPARE_TIMEOUT_MS  2500U
#define QI_VER_QUERY_TIMEOUT_MS    500U   /*!< DID 0x2013 Qi 版本查询回复超时 */

/* DID 0x2013 应答版本串：固定前缀 "QC_JYF_MCU2_FW_1.1."（19B）+
 * Qi 版本号十进制数字（1~5B）。Qi 读回版本为整数（0~任意值，2B LE
 * 上限 65535），X 直接用该整数的十进制数字替换（例：读回 3 →
 * "QC_JYF_MCU2_FW_1.1.3"；读回 12 → "QC_JYF_MCU2_FW_1.1.12"）。
 * 拼接用 memcpy+单字节赋值，勿 sprintf。 */
#define QI_VER_STR_PREFIX          "QC_JYF_MCU2_FW_1.1."
#define QI_VER_STR_PREFIX_LEN      19U
#define QI_VER_DIGITS_MAX          5U    /* uint16 十进制最多 5 位 */
#define QI_VER_STR_LEN_MAX         24U   /* 19B 前缀 + 5B 数字 */

/* 编译期自检：前缀恰 19 字节、串上限 = 前缀 + 5 位数字（改前缀/宏时当场报错） */
typedef char qi_ver_str_len_ok[(sizeof(QI_VER_STR_PREFIX) - 1U == QI_VER_STR_PREFIX_LEN)
                            && (QI_VER_STR_PREFIX_LEN + QI_VER_DIGITS_MAX == QI_VER_STR_LEN_MAX) ? 1 : -1];

static uint32_t g_qi_iap_last_tx_ms = 0U;

static uint8_t  g_qi_iap_state    = QI_IAP_IDLE;
static uint8_t  g_qi_iap_progress = 0U;
static uint16_t g_qi_iap_total    = 0U;
static uint16_t g_qi_iap_sent     = 0U;
static uint16_t g_qi_fw_version       = 0U; /*!< Qi 版本缓存（整数 0~65535）：DID 0x2133/0x2132 读出源（2B LE 原样）；0x2013 主动问询路径存完整整数值，用于拼完整版本串。由 0x01 上报解析或 0x2013 问询回复更新。版本 0 为合法值，有效性判据用 g_qi_fw_version_valid（原「!=0 判有效」取舍废弃） */
static uint8_t  g_qi_fw_version_valid = 0U; /*!< 版本缓存有效标志：0=无缓存（超时兜底回 NRC 0x22），1=已缓存（含版本 0） */

/** @brief  deferred UDS response while waiting for Qi chip ACK */
static uint32_t g_qi_iap_wait_start_ms = 0U; /*!< timestamp when WAIT_ACK entered */
static uint8_t  g_qi_iap_pending_did[2] = {0U}; /*!< DID bytes for deferred response */
static uint32_t g_qi_iap_ack_timeout_ms = QI_IAP_ACK_TIMEOUT_MS;
static uint16_t g_qi_iap_pending_chunk = 0U; /*!< bytes to add to sent after ACK */

/** @brief  DID 0x2013 Qi 版本主动问询（延迟应答）状态 */
static uint8_t  g_qi_ver_q_state    = 0U;   /*!< 0=idle, 1=waiting Qi 回复 */
static uint32_t g_qi_ver_q_start_ms = 0U;

/* ========================================================================== */
/*  Qi charging state variables                                              */
/* ========================================================================== */

static uint8_t  g_qi_charger_enable   = 0U;     /*!< DID 0x2101: volatile, reset to 0 */
static uint8_t  g_qi_charge_state     = QI_CHARGE_DISABLED;  /*!< DID 0x2102 */
static uint8_t  g_qi_device_present   = 0U;     /*!< DID 0x2103 */
static uint16_t g_qi_output_power_mw  = 0U;     /*!< DID 0x2104, mW */
static uint8_t  g_qi_voltage_raw      = 0U;     /*!< DID 0x2105 byte 0 */
static uint8_t  g_qi_current_raw      = 0U;     /*!< DID 0x2105 byte 1 */
static uint8_t  g_qi_pcb_temp         = 0U;     /*!< DID 0x2108, PCB temperature ℃ */
static uint8_t  g_qi_fod_status       = 0U;     /*!< DID 0x2109 */
static uint8_t  g_qi_fault_code       = 0U;     /*!< DID 0x210B */
static uint8_t  g_qi_thermal_derate   = 100U;   /*!< DID 0x210C, 100%=no derating */
static uint8_t  g_qi_last_fault_detail[4] = {0U};  /*!< DID 0x2110, 4-byte fault detail */

/* Qi persistent config (loaded from NVM at init) */
static uint16_t g_qi_power_limit_mw   = 1500U;  /*!< DID 0x210D, 1500 mW = 15W default */

/** @brief  SIT1145 Normal + CAN online. Power-on is Normal (idle 30 s → Standby). */
static uint8_t  g_can_awake = 0;
static uint8_t  g_need_lifecycle_announce = 0;
static uint32_t g_uds_last_ms = 0;
static uint32_t g_standby_since_ms = 0;
static uint32_t g_announce_due_ms = 0;
static uint8_t  g_lp_ever_standby = 0;
static uint8_t  g_lp_wup_count = 0;
static uint8_t  g_lp_woke_from_standby = 0;
static uint8_t  g_lp_last_wake_src = 0;
static uint16_t g_lp_last_standby_sec = 0;
/** sticky：can_lp_hold_standby 两次 standby_mode_set 失败置 1（DID 0x2119 flags bit2） */
static uint8_t  g_lp_standby_fail = 0;
/** 上电（trial/非 trial 同路径）推迟到 __enable_irq() 之后再 enter_normal：
 *  harvest/wait_cts 依赖 SysTick；init 统一置 1 */
static uint8_t  g_lp_need_online = 0;
/* ---- Round-3 anti-phantom-wake diagnostics (DID 0x211A) ------------------
 * Bench TC-S002 2026-10-01, 1.1.4 firmware (round-2 fix 9f4cf24), 6 rounds:
 * 3 PASS / 3 FAIL; every FAIL is WK 01 41 57 4B at SB+124~141ms (inhibit
 * expiry, first wake poll), src=1 (PA11/RXD low), ZERO frames on the bus,
 * and the 0x211A wake snapshot shows ev63 CW=0x01 (r4 stat=0x81: PA11 high
 * at pre-check then low inside wakeup_pending; r6 stat=0x83: CAN flag set at
 * the first pin-low pre-check). CW set with no frame on the bus = PHANTOM
 * latch: round-2's skip-clear-on-flag rule and wakeup_pending()'s PA11-first
 * shortcut both waved it through (r6 took the flag path, r4 the pin path).
 * 4B SF cap (0x2119 already fills the 7B single-frame payload) forces a
 * stat-bit repack; per-phase fields below let ONE bench run separate
 * pre-wipe latch / wipe-stuck / post-wipe relatch / clear+release outcomes.
 * Bit positions 0x01/0x02/0x08/0x10/0x20/0x80 keep round-2 meanings;
 * 0x04 (was STUCK) and 0x40 (was RELEASED) are repurposed. Reset per
 * Standby entry.
 * ------------------------------------------------------------------------ */
#define WIPE_STAT_VALID        0x01U  /* a diagnostic record exists */
#define WIPE_STAT_FLAG_PRESENT 0x02U  /* CAN flag at a pin-low check (round-3 always clears it, never skip-clears) */
#define WIPE_STAT_PREW_FLAG    0x04U  /* CAN flag already set at the unconditional inhibit-expiry wipe (latched during entry/inhibit; was round-2 STUCK) */
#define WIPE_STAT_SPI_FF       0x08U  /* SPI read back 0xFF during a check */
#define WIPE_STAT_FLAG_IN_WAIT 0x10U  /* flag 0->1 AFTER a clear = real host frame retry caught */
#define WIPE_STAT_BLOCKED      0x20U  /* wake blocked at least once (RXD stayed low, no proven-new event) */
#define WIPE_STAT_POST_STICK   0x40U  /* flag STILL set right after a clear (wipe ineffective or instant re-latch; set by both the pre-wipe and pin-low clears; was round-2 RELEASED) */
#define WIPE_STAT_WAKE_SNAP    0x80U  /* [ev24/ev63] overwritten at wake decision */
static uint8_t  g_lp_wipe_ev24 = 0xFFU;   /* 0x24 TRANSCEIVER_EVENT snapshot */
static uint8_t  g_lp_wipe_ev63 = 0xFFU;   /* 0x63 TRX_EVENT_STATUS snapshot */
static uint8_t  g_lp_wipe_stat = 0U;      /* WIPE_STAT_* bit set */
static uint8_t  g_lp_wipe_attempts = 0U;  /* clear+wait attempts this Standby (sat., includes one-shot wipe) */
static uint32_t g_lp_wipe_last_ms = 0U;   /* tick of last clear attempt (throttle) */
/** round-3: one-shot unconditional event wipe (0x24/0x63/0x64/0x61) runs on
 *  the first poll at inhibit expiry; armed by can_lp_hold_standby() on every
 *  Standby entry, consumed in can_protocol_poll() */
static uint8_t  g_lp_pre_wipe_due = 0U;

/** ignore self-wake for a short window after entering Standby */
#define CAN_LP_WAKE_INHIBIT_MS  100U
/** min interval between stale-pin clear+wait attempts while RXD is stuck:
 *  the tight main loop would otherwise re-run the <=5ms busy-wait every
 *  iteration and starve qi_uart_poll/can_driver_poll */
#define CAN_LP_WIPE_RETRY_MS    100U
/** send BOOTUP after UDS has a chance to ACK/reply the wake frame */
#define CAN_LP_ANNOUNCE_DELAY_MS  100U
/** after CAN online, spin-poll RX so host hardware retransmit of 10 01 can be ACKed */
#define CAN_LP_RX_HARVEST_MS      30U

/** 30 s with no UDS RX/TX → SIT1145 Standby (ISO 11898-2 WUP can wake)
 *  受 CAN_LP_STANDBY_ENABLE 总开关控制（can_protocol.h）：
 *  =1（含未定义，生产语义）：上电即 Normal，仅空闲超时进 Standby；
 *  =0：空闲停机整段不编译 */
#define CAN_LP_IDLE_TIMEOUT_MS  (30UL * 1000UL)

static void can_lp_mark_uds(void)
{
  g_uds_last_ms = timer_get_tick();
}

static uint8_t g_lp_ident_sent;

/**
 * @brief  send one lifecycle marker frame on 0x18FF260D
 * @note   Completion is confirmed by handle (can_driver_wait_tx_frame),
 *         not by can_driver_wait_tx_idle: right after enqueue the
 *         controller may not have picked the frame up yet, so tstat still
 *         reads IDLE/TRANSMITTED from the previous frame and wait_tx_idle
 *         returns instantly. The caller then takes the CAN offline before
 *         the frame ever reaches the bus (root cause of the missing SB
 *         marker on Standby entry).
 * @retval 1 = frame enqueued and confirmed transmitted, 0 = send rejected
 *         or completion wait failed/aborted
 */
static uint8_t can_lp_tx_marker(uint8_t b0, uint8_t b2, uint8_t b3,
                                uint8_t b4, uint8_t b5, uint8_t b6, uint8_t b7)
{
  uint8_t d[8];
  uint8_t handle;

  memset(d, 0, sizeof(d));
  d[0] = b0;
  d[1] = 0x41U;
  d[2] = b2;
  d[3] = b3;
  d[4] = b4;
  d[5] = b5;
  d[6] = b6;
  d[7] = b7;
  if (can_driver_send(CAN_ID_LIFECYCLE_BROADCAST, d, 8) != 0)
  {
    return 0U;
  }
  if (can_driver_last_tx_handle(&handle) != 0)
  {
    return 0U;
  }
  if (can_driver_wait_tx_frame(handle, 20U) != 0)
  {
    return 0U;
  }
  return 1U;
}

/** 识别帧打到 0x18FF260D。harvest 结束只能走这条，避免抢在 50 01 前面占 UDS ID */
static void can_lp_send_ident_bus(void)
{
  if (g_lp_woke_from_standby != 0U)
  {
    can_lp_tx_marker(LIFECYCLE_BOOTUP, 0x57U, 0x4BU, g_lp_wup_count,
                     g_lp_last_wake_src,
                     (uint8_t)(g_lp_last_standby_sec & 0xFFU),
                     (uint8_t)((g_lp_last_standby_sec >> 8) & 0xFFU));
  }
  else
  {
    can_lp_tx_marker(LIFECYCLE_BOOTUP, 0U, 0U, 0U, 0U, 0U, 0U);
  }
  g_need_lifecycle_announce = 0U;
}

/** 50 01 之后再发：18FF260D + UDS 响应 ID 上的 ISO-TP SF（01 41 …） */
static void can_lp_send_ident(void)
{
  uint8_t uds[8];
  uint8_t i;

  can_lp_send_ident_bus();

  for (i = 0U; i < 8U; i++)
  {
    uds[i] = 0xCCU;
  }

  if (g_lp_woke_from_standby != 0U)
  {
    uds[0] = 0x07U;
    uds[1] = LIFECYCLE_BOOTUP;
    uds[2] = 0x41U;
    uds[3] = 0x57U;
    uds[4] = 0x4BU;
    uds[5] = g_lp_wup_count;
    uds[6] = g_lp_last_wake_src;
    uds[7] = (uint8_t)(g_lp_last_standby_sec & 0xFFU);
  }
  else
  {
    uds[0] = 0x03U;
    uds[1] = LIFECYCLE_BOOTUP;
    uds[2] = 0x41U;
    uds[3] = 0x00U;
  }

  (void)can_driver_send(CAN_PROTO_UDS_RESPONSE, uds, 8);
  (void)can_driver_wait_tx_idle(20U);
  g_lp_ident_sent = 1U;
}

static void can_lp_enter_normal(void)
{
  uint8_t retry;
  uint32_t t0;
  uint32_t now;
  uint32_t dur_sec;

  if (g_can_awake != 0U)
  {
    return;
  }

  now = timer_get_tick();
  if (g_lp_ever_standby != 0U)
  {
    dur_sec = (now - g_standby_since_ms) / 1000U;
    if (dur_sec > 0xFFFFU)
    {
      dur_sec = 0xFFFFU;
    }
    g_lp_last_standby_sec = (uint16_t)dur_sec;
    if (g_lp_wup_count < 0xFFU)
    {
      g_lp_wup_count++;
    }
    g_lp_woke_from_standby = 1U;
  }
  else
  {
    g_lp_woke_from_standby = 0U;
  }

  /* TXD 必须先回到 CAN AF，再切 Normal */
  can_driver_pins_active();

  /* Official NormalMode_Set also rewrites CANCtrl (PNCOK=INVALID, CPNC=DIS,
   * CMC=active) before switching. No rewrite needed here: the wake path
   * only touches 0x23 EVENT_EN (sit1145_wake_enable) and 0x01 MODE_CONTROL,
   * so CANCtrl still holds the init value, which equals that official
   * sequence. CTS is already validated inside sit1145_normal_mode_set. */
  for (retry = 0U; retry < 3U; retry++)
  {
    if (sit1145_normal_mode_set() != 0U)
    {
      break;
    }
    t0 = timer_get_tick();
    while ((timer_get_tick() - t0) < 10U) { __NOP(); }
  }

  /* 清 CW，等 RXD 从唤醒强制低恢复成隐性，再开 CAN，否则会 bus-off */
  sit1145_wakeup_clear();
  t0 = timer_get_tick();
  while ((timer_get_tick() - t0) < 5U)
  {
    if (gpio_input_data_bit_read(GPIOA, GPIO_PINS_11) != RESET)
    {
      break;
    }
  }

  can_driver_online();
  g_can_awake = 1U;
  can_lp_mark_uds();
  g_lp_ident_sent = 0U;
  g_need_lifecycle_announce = 1U;
  g_announce_due_ms = timer_get_tick() + CAN_LP_ANNOUNCE_DELAY_MS;

  /* Standby 下第一帧只当 WUP，MCU 收不到。主机 CAN 控制器会无 ACK 重发，
   * 这里空转收 RX，赶在重发窗口内 ACK 并回 50 01；quiet 期内禁止 BOOTUP。
   * trial 上电不是 WUP，且 SysTick 未跑时 harvest 会死等，跳过。 */
  if (g_lp_ever_standby != 0U)
  {
    t0 = timer_get_tick();
    while ((timer_get_tick() - t0) < CAN_LP_RX_HARVEST_MS)
    {
      can_driver_poll();
    }
  }

  if (g_lp_ident_sent == 0U)
  {
    /* 10 01 若还在重发路上，UDS ID 必须留给 50 01 */
    can_lp_send_ident_bus();
  }
}

/* Standby 进入函数：受 CAN_LP_STANDBY_ENABLE 总开关控制（can_protocol.h）。
 * =1（含未定义，生产语义）：仅空闲超时进 Standby，上电即 Normal；
 * =0：空闲停机整段不编译，源码完整保留，唤醒/恢复路径不受影响。 */
#if !defined(CAN_LP_STANDBY_ENABLE) || (CAN_LP_STANDBY_ENABLE != 0U)
static void can_lp_hold_standby(void)
{
  uint32_t t0;

  /* Sequence follows the official FAE SIT1145 example (SleepMode_Set):
   * clear every wake/event register -> enable wake detection -> switch mode.
   * 1) MCU CAN offline first: no ACK/TX while the transceiver is reconfigured.
   *    Keep it before any GPIO change — moving TXD to GPIO while still in
   *    Normal would drive the bus dominant. */
  can_driver_offline();

  /* 2) Full event wipe BEFORE enabling wake / switching mode. The old code
   *    only cleared 0x24 CW/WUF and 0x63 CW, and only after the mode switch;
   *    a residual 0x64 WAKE-pin event (never cleared anywhere, incl. init)
   *    keeps RXD forced low for the whole wake period, so the first poll
   *    after the 100 ms inhibit saw PA11 low and self-woke immediately. */
  sit1145_wakeup_clear();

  /* 3) Enable standard CAN wake (CWE @0x23) only after the flags are clean */
  sit1145_wake_enable();

  /* 4) Switch to Standby: write + readback inside sit1145_set_mode. On
   *    failure retry once after 1 ms (same pattern as the old init step 10).
   *    Two failures still continue teardown (transceiver state unknown, but
   *    the MCU side must go offline) and raise the sticky flag
   *    g_lp_standby_fail, observable via DID 0x2119 flags bit2. */
  if (sit1145_standby_mode_set() == 0U)
  {
    t0 = timer_get_tick();
    while ((timer_get_tick() - t0) < 1U) { __NOP(); }
    if (sit1145_standby_mode_set() == 0U)
    {
      g_lp_standby_fail = 1U;
    }
  }

  /* 5) Pin switch after mode change (TXD must not be GPIO in Normal) */
  can_driver_pins_standby();

  /* 6) Settle + final event wipe: the mode/pin transition above can latch
   *    CW asynchronously while the transceiver settles, AFTER a single
   *    immediate clear — bench 1.1.4 (round-2, 6 rounds 3PASS/3FAIL,
   *    FAIL@SB+124~141ms src=1 ev63=0x01 zero frames) is consistent with a
   *    latch landing in that window. Round-3 nets:
   *      6a) wipe whatever the transition already latched;
   *      6b) bounded <=5ms settle/release wait (returns immediately when
   *          RXD is already released — the clean common case, adds no
   *          entry latency there);
   *      6c) second wipe covering anything latched during the wait.
   *    A latch still surviving 6c is caught by the one-shot unconditional
   *    wipe at inhibit expiry (g_lp_pre_wipe_due in can_protocol_poll). */
  sit1145_wakeup_clear();
  t0 = timer_get_tick();
  while ((timer_get_tick() - t0) < 5U)
  {
    if (gpio_input_data_bit_read(GPIOA, GPIO_PINS_11) != RESET)
    {
      break;
    }
  }
  sit1145_wakeup_clear();

  g_can_awake = 0U;
  g_standby_since_ms = timer_get_tick();
  g_lp_ever_standby = 1U;
  /* reset round-3 wipe diagnostics for this Standby entry */
  g_lp_wipe_ev24 = 0xFFU;
  g_lp_wipe_ev63 = 0xFFU;
  g_lp_wipe_stat = 0U;
  g_lp_wipe_attempts = 0U;
  g_lp_wipe_last_ms = 0U;
  g_lp_pre_wipe_due = 1U;
}

static void can_lp_enter_standby(void)
{
  if (g_can_awake != 0U)
  {
    (void)can_driver_wait_tx_idle(20U);
    session_reset_to_default();
    /* SB marker (06 41 53 42) must be confirmed on the wire before CAN goes
     * offline, otherwise the bus never shows SB ahead of the silence. Send
     * is handle-confirmed; retry once if enqueue/completion fails. */
    if (can_lp_tx_marker(LIFECYCLE_SHUTDOWN, 0x53U, 0x42U,
                         g_lp_wup_count, 0U, 0U, 0U) == 0U)
    {
      (void)can_driver_wait_tx_idle(20U);
      (void)can_lp_tx_marker(LIFECYCLE_SHUTDOWN, 0x53U, 0x42U,
                             g_lp_wup_count, 0U, 0U, 0U);
    }
  }
  can_lp_hold_standby();
}
#endif /* CAN_LP_STANDBY_ENABLE */

/* ========================================================================== */
/*  Private helper functions                                                 */
/* ========================================================================== */

/**
 * @brief  send a UDS response frame
 * @param  data: pointer to response data
 * @param  len: data length
 * @retval none
 */
#define PROTO_TX_PEND_MAX  256U
static uint8_t  g_tx_pend[PROTO_TX_PEND_MAX];
static uint16_t g_tx_pend_len = 0U;

static void proto_flush_pending_tx(void)
{
  uint16_t n = g_tx_pend_len;

  if (n == 0U)
  {
    return;
  }
  g_tx_pend_len = 0U;
  (void)isotp_tx_send(CAN_PROTO_UDS_RESPONSE, g_tx_pend, n);
}

static void proto_send_response(uint8_t *data, uint16_t len)
{
  can_lp_mark_uds();
  if ((data == (uint8_t *)0) || (len == 0U) || (len > PROTO_TX_PEND_MAX))
  {
    return;
  }
  /* SF can send from RX callback. MF must wait for Flow Control — if we
   * block inside can_driver_poll's callback, FC sits in the same FIFO
   * and N_Bs times out (27 01 34-byte seed looks like "no response"). */
  if (len <= 7U)
  {
    (void)isotp_tx_send(CAN_PROTO_UDS_RESPONSE, data, len);
    return;
  }
  memcpy(g_tx_pend, data, len);
  g_tx_pend_len = len;
}

/**
 * @brief  send UDS negative response
 * @param  service_id: the rejected service ID
 * @param  nrc: negative response code
 * @retval none
 */
static void proto_send_nrc(uint8_t service_id, uint8_t nrc)
{
  uint8_t resp[3];
  resp[0] = UDS_NEGATIVE_RESPONSE;
  resp[1] = service_id;
  resp[2] = nrc;
  proto_send_response(resp, 3);
}

/**
 * @brief  Qi 版本回复数据 → 整数版本值（稳健化）
 * @note   Qi 版本号语义为整数（0~任意值，2B LE 上限 65535），非个位数。
 *         兼容两种载荷形态：
 *         1) ASCII 十进制数字串（如 "12"）：全字节为 '0'~'9' 时按十进制
 *            解析（溢出饱和到 65535）；
 *         2) 小端整数：规格形态 2B LE；1B 退化为该字节数值。
 * @param  data: 回复数据指针
 * @param  len:  数据字节数
 * @retval 版本整数值 0~65535（len==0 或空指针返回 0）
 */
static uint16_t qi_ver_parse_value(const uint8_t *data, uint8_t len)
{
  uint8_t  i;
  uint8_t  all_ascii = 1U;
  uint32_t v = 0U;

  if ((data == (const uint8_t *)0) || (len == 0U))
  {
    return 0U;
  }
  for (i = 0U; i < len; i++)
  {
    if ((data[i] < (uint8_t)'0') || (data[i] > (uint8_t)'9'))
    {
      all_ascii = 0U;
      break;
    }
  }
  if (all_ascii != 0U)
  {
    for (i = 0U; i < len; i++)
    {
      v = (v * 10U) + (uint32_t)(data[i] - (uint8_t)'0');
      if (v > 0xFFFFU)
      {
        return 0xFFFFU;                 /* 饱和：规格 2B LE，超出按上限 */
      }
    }
    return (uint16_t)v;
  }
  /* 小端整数（规格 2B LE）：取低 2B */
  v = (uint32_t)data[0];
  if (len >= 2U)
  {
    v |= (uint32_t)((uint32_t)data[1] << 8);
  }
  return (uint16_t)v;
}

/**
 * @brief  整数版本值 → 十进制数字串（逆序回填，勿 sprintf）
 * @param  ver: 版本整数值 0~65535
 * @param  out: 输出缓冲（至少 QI_VER_DIGITS_MAX 字节），高位在前
 * @retval 数字位数 1~5
 */
static uint8_t qi_ver_format_digits(uint16_t ver, char *out)
{
  char    tmp[QI_VER_DIGITS_MAX];
  uint8_t n = 0U;
  uint8_t i;

  do
  {
    tmp[n] = (char)('0' + (char)(ver % 10U));
    ver    = (uint16_t)(ver / 10U);
    n++;
  } while ((ver != 0U) && (n < QI_VER_DIGITS_MAX));

  for (i = 0U; i < n; i++)
  {
    out[i] = tmp[(uint8_t)(n - 1U - i)];
  }
  return n;
}

/**
 * @brief  发 DID 0x2013 完整正响应：62 20 13 + ASCII "QC_JYF_MCU2_FW_1.1.X"
 * @note   X 为 Qi 版本整数的十进制数字（1~5B），响应 3+19+1~5 = 23~27B > 7，
 *         proto_send_response 自动走 g_tx_pend 多帧延迟路径
 *         （can_protocol_poll 末尾 proto_flush_pending_tx 泵出，27 服务
 *         34 字节 seed/key 同机制）。严禁在帧回调里直接 isotp_tx_send
 *         多帧（FC 会卡死，见 proto_send_response 注释）。
 * @param  ver: Qi 版本整数值 0~65535
 * @retval none
 */
static void qi_ver_send_full_response(uint16_t ver)
{
  uint8_t  resp[3U + QI_VER_STR_LEN_MAX];   /* 27B 上限: 62 20 13 + 24B ASCII */
  char     digits[QI_VER_DIGITS_MAX];
  uint8_t  ndigits;
  uint16_t total;

  ndigits = qi_ver_format_digits(ver, digits);
  total   = (uint16_t)(3U + QI_VER_STR_PREFIX_LEN + ndigits);

  resp[0] = UDS_SID_READ_DATA_BY_ID + UDS_POSITIVE_RESPONSE_OFFSET;
  resp[1] = 0x20U;
  resp[2] = 0x13U;
  memcpy(&resp[3], QI_VER_STR_PREFIX, QI_VER_STR_PREFIX_LEN);
  memcpy(&resp[3U + QI_VER_STR_PREFIX_LEN], digits, ndigits);
  proto_send_response(resp, total);
}

static uint8_t  g_long_op_sid;
static uint32_t g_long_op_last_pending_ms = 0U;

/** @brief  P2* 补发阈门：距上次 0x78 超过此时长才允许补发下一帧 */
#define LONG_OP_PENDING_REFRESH_MS  4500U

/**
 * @brief  recover CAN after Flash stall (IRQ may have missed bus-off)
 */
static void proto_can_busoff_recover(void)
{
  uint32_t start;
  uint8_t n;

  /* 250kbps 下 bus-off 恢复需 128×11 个隐性位（≈5.6ms 总线时间）；
   * 单 Bank Flash 擦除 stall 后控制器状态复位更慢，原 3×10ms 窗口
   * 系统性不足，导致每次都掉进下面的兜底分支。5×20ms 覆盖最坏情况。 */
  for (n = 0U; n < 5U; n++)
  {
    if (can_busoff_get(CAN1) == RESET)
    {
      return;
    }
    can_busoff_reset(CAN1);
    start = timer_get_tick();
    while ((timer_get_tick() - start) < 20U)
    {
      if (can_busoff_get(CAN1) == RESET)
      {
        return;
      }
    }
  }
  /* 恢复失败时禁止走 can_driver_init()：那是上电初始化路径，会清掉
   * rx_callback/busoff_recovery_cb 并把 CAN 停在 software reset 等待
   * can_driver_online()——0x31 擦槽循环中一旦触发，本函数后续泵出的
   * 0x78 与尾部 0x71 正响应全部黑洞；返回主循环后 RX 回调已丢，
   * 设备 UDS 永久失聪，只有断电才能恢复（06:52 擦除 55s 全静默根因）。
   * offline→online 只复位 CAN 外设+清 pending+重挂 RX/ERR 中断，
   * rx_callback/busoff_recovery_cb 保持不变，长操作路径可自愈。 */
  can_driver_offline();
  can_driver_online();
}

/**
 * @brief  NRC 0x78 as a raw ISO-TP SF (do not use isotp_tx_send; it can block 1s)
 */
static void proto_send_pending(uint8_t service_id)
{
  uint8_t sf[8];
  sf[0] = 0x03U;
  sf[1] = UDS_NEGATIVE_RESPONSE;
  sf[2] = service_id;
  sf[3] = UDS_NRC_RESPONSE_PENDING;
  sf[4] = 0xCCU;
  sf[5] = 0xCCU;
  sf[6] = 0xCCU;
  sf[7] = 0xCCU;
  (void)can_driver_send(CAN_PROTO_UDS_RESPONSE, sf, 8);
}

static void proto_begin_long_op(uint8_t service_id)
{
  g_long_op_sid = service_id;
  proto_can_busoff_recover();
  (void)sit1145_normal_mode_set();
  proto_send_pending(service_id);
  g_long_op_last_pending_ms = timer_get_tick();
  (void)can_driver_wait_tx_idle(50U);
}

static void proto_end_long_op(void)
{
  proto_can_busoff_recover();
  (void)sit1145_normal_mode_set();
  (void)can_driver_wait_tx_idle(50U);
}

void can_proto_pump_long_op(void)
{
  /* 时间闸门：仅在距上次 0x78 接近 P2*（4500ms）时才补发，
   * 防止擦除循环/主循环以毫秒级频率洪泛 NRC 0x78（UDS 规范：
   * 首次 0x78 后应在 P2* 内返回最终响应，仅当处理将再次
   * 超过 P2* 时才需再次发送 0x78）。 */
  if ((timer_get_tick() - g_long_op_last_pending_ms) < LONG_OP_PENDING_REFRESH_MS)
  {
    return;
  }
  proto_can_busoff_recover();
  (void)sit1145_normal_mode_set();
  proto_send_pending(g_long_op_sid);
  g_long_op_last_pending_ms = timer_get_tick();
  (void)can_driver_wait_tx_idle(20U);
}

void can_proto_send_response(uint8_t *data, uint16_t len)
{
  proto_send_response(data, len);
}

void can_proto_send_nrc(uint8_t service_id, uint8_t nrc)
{
  proto_send_nrc(service_id, nrc);
}

void can_proto_begin_long_op(uint8_t service_id)
{
  proto_begin_long_op(service_id);
}

void can_proto_end_long_op(void)
{
  proto_end_long_op();
}

void can_proto_send_pending(uint8_t service_id)
{
  proto_send_pending(service_id);
}

uint8_t can_proto_security_unlocked(void)
{
  return security_unlocked;
}

uint8_t can_proto_in_programming(void)
{
  return (current_session == SESSION_PROGRAMMING) ? 1U : 0U;
}

/**
 * @brief  reset session to default and clear security state
 * @note   called on session timeout or switch to default session
 * @retval none
 */
static void session_reset_to_default(void)
{
  current_session   = SESSION_DEFAULT;
  security_unlocked = 0;
  g_seed_generated  = 0;
  g_seed_sub        = 0;
  g_tx_pend_len     = 0U;
  ota_dl_abort();
}

/**
 * @brief  handle session switch rules per spec section 7.3
 * @param  new_session: requested session type
 * @retval none
 */
static void session_switch(uint8_t new_session)
{
  if (new_session == SESSION_DEFAULT)
  {
    /* entering default: abort any firmware transfer, clear security */
    /* (OTA transfer is not active in APP, but clear state anyway) */
    session_reset_to_default();
  }
  else if (current_session != SESSION_DEFAULT && new_session != current_session)
  {
    /* non-default -> different non-default: clear security */
    security_unlocked = 0;
    current_session = new_session;
  }
  else
  {
    /* default -> non-default, or same session: just update */
    current_session = new_session;
  }
}


/**
 * @brief  copy string into response buffer (including null terminator)
 * @param  dst: destination buffer
 * @param  src: source string
 * @retval number of bytes written (including null terminator)
 */
static uint32_t generate_random_seed(void)
{
  static uint32_t lfsr = 0xA5A5A5A5U;
  uint32_t tick = timer_get_tick();
  uint32_t bit;

  lfsr ^= tick;
  bit = ((lfsr >> 0) ^ (lfsr >> 1) ^ (lfsr >> 21) ^ (lfsr >> 31)) & 1U;
  lfsr = (lfsr >> 1) | (bit << 31);
  lfsr ^= (tick << 7) ^ (tick >> 13);
  return lfsr;
}

/* ========================================================================== */
/*  NVM persistence helpers for Qi config DIDs                               */
/* ========================================================================== */

/**
 * @brief  load persistent Qi config from NVM
 */
static void qi_nvm_load_config(void)
{
  uint8_t buf[8];

  if (nvm_drv_is_valid() != 0U)
  {
    if (nvm_drv_read(NVM_OFFSET_POWER_LIMIT, buf, 2U) == NVM_STATUS_OK)
    {
      uint16_t val = (uint16_t)buf[0] | ((uint16_t)buf[1] << 8);
      if ((val == 500U) || (val == 1000U) || (val == 1500U))
      {
        g_qi_power_limit_mw = val;
      }
    }
  }
}

/**
 * @brief  save one persistent Qi config field to NVM
 * @param  offset: NVM offset
 * @param  data: pointer to data
 * @param  len: data length
 * @retval 0=ok, -1=error
 */
static int8_t qi_nvm_save(uint16_t offset, const uint8_t *data, uint16_t len)
{
  if (nvm_drv_write(offset, (uint8_t *)data, len) != NVM_STATUS_OK)
  {
    return -1;
  }
  return 0;
}

static int8_t fill_did_payload(uint16_t did, uint8_t *out, uint8_t *olen)
{
  device_info_t di;

  switch (did)
  {
    case DID_SW_VERSION:
    {
      /* 版本唯一真相源 = APP 编译时常量 SW_VERSION_STR（本文件顶部）。
       * OTA metadata / XATO 镜像头 version 字段不作版本来源：metadata 会被
       * trial/rollback/defaults 重建改写，双副本全坏时会被默认值破坏性覆盖；
       * 镜像头 version 仅保留镜像标识/打包校验用途，不代表运行代码版本。 */
      device_info_pad32(out, SW_VERSION_STR);
      *olen = 32U;
      return 0;
    }
    case DID_BOOTLOADER_VERSION:
      device_info_pad32(out, BOOTLOADER_VER_STR);
      *olen = 32U;
      return 0;
    case DID_HW_VERSION:
      /* HW_VERSION_STR 是 F193 唯一真相源（对齐 F195/SW_VERSION_STR 规约）。
       * NVM device_info.hw_version 已弃用：字段仅 8B，装不下 15B 全串
       * "QC_JYF_HW_1.1.5"，且 device_info_write_sn / device_info_write_pubkey
       * 建块分支会 memset 清零该字段（首写 SN/pubkey 后 NVM 读回全空）。
       * 此处恒定返回编译时常量，任何写入（SN/pubkey 等）都无法改变 F193 读值。 */
      device_info_pad32(out, HW_VERSION_STR);
      *olen = 32U;
      return 0;
    case DID_SERIAL_NUMBER:
      if (device_info_read(&di) != 0)
      {
        return -1;
      }
      device_info_pad32(out, di.sn);
      *olen = 32U;
      return 0;
    case DID_FW_TYPE:
      out[0] = FW_TYPE_APP;
      *olen = 1U;
      return 0;
    case DID_OTA_STATE:
    {
      ota_metadata_t meta;
      uint8_t status;
      if (ota_metadata_read(&meta) != 0U)
      {
        out[0] = 0xFFU;
        *olen = 1U;
        return 0;
      }
      /* OTA Status DID 0x2112 八状态定义:
       * 0x00 Idle               无 OTA 操作，无待报告结果
       * 0x01 Downloading        正在下载固件到备份区
       * 0x02 Validating         传输完成，正在验证（CRC/签名）
       * 0x03 Pending Activation 验签提交成功，随即自复位激活
       * （2026-09-23 起自动复位，无 11 01 等待）
       * 0x04 Trial Boot         新 APP 启动未确认（单 App 架构不适用）
       * 0x05 Confirmed          OTA 搬运成功，新固件已生效
       * 0x06 Rolled Back        搬运失败，已回滚到旧固件
       * 0x07 Failed             下载或验证失败，旧固件保留 */
      if (meta.ota_state == OTA_STATE_DOWNLOADING)
      {
        status = 0x01U;  /* Downloading */
      }
      else if (meta.backup_valid != 0U)
      {
        status = 0x03U;  /* Pending Activation */
      }
      else if (meta.last_boot_reason == 0x03U)
      {
        status = 0x05U;  /* Confirmed: OTA 搬运成功 */
      }
      else if (meta.last_boot_reason == 0x04U)
      {
        status = 0x06U;  /* Rolled Back: 搬运失败回滚 */
      }
      else
      {
        status = 0x00U;  /* Idle */
      }
      out[0] = status;
      *olen = 1U;
      return 0;
    }
    case DID_ACTIVE_SLOT:
      out[0] = ota_running_slot();
      *olen = 1U;
      return 0;
    case DID_PENDING_SLOT:
    {
      ota_metadata_t meta;
      if (ota_dl_erased() != 0U)
      {
        out[0] = ota_dl_target_slot();
      }
      else
      {
        out[0] = (ota_metadata_read(&meta) == 0) ?
                 ((meta.backup_valid != 0U) ? 0x02U : 0xFEU) : 0xFEU;
      }
      *olen = 1U;
      return 0;
    }
    case DID_LAST_BOOT_REASON:
    {
      ota_metadata_t meta;
      out[0] = (ota_metadata_read(&meta) == 0) ? meta.last_boot_reason : 0xFFU;
      *olen = 1U;
      return 0;
    }
    case DID_ROLLBACK_COUNT:
    {
      ota_metadata_t meta;
      out[0] = (ota_metadata_read(&meta) == 0) ?
               (uint8_t)(meta.copy_retry_count & 0xFFU) : 0U;
      *olen = 1U;
      return 0;
    }
    case DID_CLAMP_STATE:
      /* PA0 low (magnetic field/phone present) → 0x00, PA0 high (no phone) → 0x01 */
      out[0] = (gpio_input_data_bit_read(GPIOA, GPIO_PINS_0) != RESET) ? 0x01U : 0x00U;
      *olen = 1U;
      return 0;
    case DID_SIT1145_LP_STATUS:
      /* [0] bit0=ever_standby bit1=last_wake_was_wup bit2=standby_fail(sticky)
       * [1] wup_count  [2-3] last_standby_sec LE */
      out[0] = (uint8_t)((g_lp_ever_standby != 0U) | ((g_lp_woke_from_standby != 0U) << 1) |
                         ((g_lp_standby_fail != 0U) << 2) |
                         ((g_lp_last_wake_src & 0x0FU) << 4));
      out[1] = g_lp_wup_count;
      out[2] = (uint8_t)(g_lp_last_standby_sec & 0xFFU);
      out[3] = (uint8_t)((g_lp_last_standby_sec >> 8) & 0xFFU);
      *olen = 4U;
      return 0;
    case DID_SIT1145_LP_WIPE_DIAG:
      /* Round-3 anti-phantom-wake diagnostics (bench TC-S002 2026-10-01,
       * 1.1.4 fw, 6 rounds 3PASS/3FAIL, FAIL@SB+124~141ms src=1
       * ev63=0x01 zero frames; 0x211A r4=0x81 r6=0x83). Kept SF-safe at 4B
       * because 0x2119 already fills the 7-byte single-frame payload and an
       * appended byte would force ISO-TP multi-frame (current bench reader
       * sends no Flow Control). [0]/[1] semantics:
       * [0] 0x24 TRANSCEIVER_EVENT snapshot - pre-wipe readback, then last
       *     pin-low/clear-wait check, or the wake decision if [2] bit7 set
       * [1] 0x63 TRX_EVENT_STATUS   snapshot (same source as [0])
       * [2] stat bits (WIPE_STAT_* in can_protocol.c):
       *     bit0 record valid | bit1 CAN flag at a pin-low check
       *     bit2 (0x04) flag already set at the unconditional inhibit-expiry
       *         wipe = latched during entry/inhibit (was round-2 STUCK)
       *     bit3 SPI read 0xFF | bit4 flag 0->1 after a clear = real retry
       *     bit5 wake blocked at least once
       *     bit6 flag still set after the wipe = wipe ineffective/re-latch
       *         (was round-2 RELEASED) | bit7 snapshot at wake decision
       *     PA11 itself: flags are read first, PA11 second (round-3), so no
       *     stat bit implies PA11 low; PA11 at wake time = WK src byte
       * [3] clear+wait attempt count this Standby (saturating, includes
       *     the one-shot unconditional wipe) */
      out[0] = g_lp_wipe_ev24;
      out[1] = g_lp_wipe_ev63;
      out[2] = g_lp_wipe_stat;
      out[3] = g_lp_wipe_attempts;
      *olen = 4U;
      return 0;
    case DID_ECDSA_PUBKEY:
    {
      if (device_info_read(&di) != 0)
      {
        return -1;
      }
      if (di.pubkey_valid != 0x01U)
      {
        return -1;
      }
      memcpy(out, di.ecdsa_pubkey, 65U);
      *olen = 65U;
      return 0;
    }
    case DID_CHARGER_CAPABILITY:
      out[0] = 15U;   /* max power 15W */
      out[1] = 0U;
      out[2] = 0U;
      out[3] = 0U;
      *olen = 4U;
      return 0;
    case DID_CHARGE_STATE:
      /* read PB2 actual level for charge state */
      if (gpio_output_data_bit_read(GPIOB, GPIO_PINS_2) != RESET)
        out[0] = QI_CHARGE_CHARGING;  /* PB2 high → CHARGING */
      else if (g_qi_charger_enable != 0U)
        out[0] = QI_CHARGE_STANDBY;   /* enabled but no device */
      else
        out[0] = QI_CHARGE_DISABLED;  /* disabled */
      *olen = 1U;
      return 0;
    case DID_DEVICE_PRESENT:
      out[0] = g_qi_device_present;
      *olen = 1U;
      return 0;
    case DID_OUTPUT_POWER:
      out[0] = (uint8_t)(g_qi_output_power_mw & 0xFFU);
      out[1] = (uint8_t)((g_qi_output_power_mw >> 8) & 0xFFU);
      *olen = 2U;
      return 0;
    case DID_INPUT_VI:
      out[0] = g_qi_voltage_raw;
      out[1] = g_qi_current_raw;
      *olen = 2U;
      return 0;
    case DID_INPUT_CURRENT:
    case DID_COIL_TEMP:
    case DID_ALIGNMENT:
      /* HW not supported */
      return -1;
    case DID_PCB_TEMP:
      out[0] = g_qi_pcb_temp;
      *olen = 1U;
      return 0;
    case DID_FOD_STATUS:
      out[0] = g_qi_fod_status;
      *olen = 1U;
      return 0;
    case DID_FAULT_CODE:
      out[0] = g_qi_fault_code;
      *olen = 1U;
      return 0;
    case DID_THERMAL_DERATE:
      out[0] = g_qi_thermal_derate;
      *olen = 1U;
      return 0;
    case DID_POWER_LIMIT:
      out[0] = (uint8_t)(g_qi_power_limit_mw & 0xFFU);
      out[1] = (uint8_t)((g_qi_power_limit_mw >> 8) & 0xFFU);
      *olen = 2U;
      return 0;
    case DID_LAST_FAULT_DETAIL:
      memcpy(out, g_qi_last_fault_detail, 4U);
      *olen = 4U;
      return 0;
    case 0x21FFU:
      out[0] = sit1145_get_mode();  /* 0x04=Standby, 0x07=Normal, 0x01=Sleep */
      *olen = 1U;
      return 0;
    case DID_QI_IAP_STATUS:
      /* [0]state [1]progress [2-3]Qi版本 LE [4-5]已发 LE [6-7]总长 LE */
      out[0] = (g_qi_iap_state == QI_IAP_WAIT_ACK) ? QI_IAP_IN_PROGRESS : g_qi_iap_state;
      out[1] = g_qi_iap_progress;
      out[2] = (uint8_t)(g_qi_fw_version & 0xFFU);
      out[3] = (uint8_t)((g_qi_fw_version >> 8) & 0xFFU);
      out[4] = (uint8_t)(g_qi_iap_sent & 0xFFU);
      out[5] = (uint8_t)((g_qi_iap_sent >> 8) & 0xFFU);
      out[6] = (uint8_t)(g_qi_iap_total & 0xFFU);
      out[7] = (uint8_t)((g_qi_iap_total >> 8) & 0xFFU);
      *olen = 8U;
      return 0;
    case DID_QI_FW_VERSION:
      out[0] = (uint8_t)(g_qi_fw_version & 0xFFU);
      out[1] = (uint8_t)((g_qi_fw_version >> 8) & 0xFFU);
      *olen = 2U;
      return 0;
    default:
      return -1;
  }
}

/* ========================================================================== */
/*  UDS service handlers                                                     */
/* ========================================================================== */

/**
 * @brief  DiagnosticSessionControl (0x10)
 * @param  data: UDS payload
 * @param  len:  payload length
 * @retval none
 */
static void handle_diag_session_ctrl(uint8_t *data, uint16_t len)
{
  uint8_t resp[8];
  uint8_t sub_func;
  uint8_t suppress;
  uint8_t session_type;

  if (len < 2U)
  {
    proto_send_nrc(UDS_SID_DIAG_SESSION_CTRL, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
    return;
  }

  sub_func     = data[1];
  suppress     = sub_func & UDS_SUBFUNC_SUPPRESS_POS_RESP;
  session_type = sub_func & UDS_SUBFUNC_MASK;

  /* validate session type */
  if ((session_type != SESSION_DEFAULT) &&
      (session_type != SESSION_PROGRAMMING) &&
      (session_type != SESSION_EXTENDED))
  {
    proto_send_nrc(UDS_SID_DIAG_SESSION_CTRL, UDS_NRC_SUBFUNCTION_NOT_SUPPORTED);
    return;
  }

  /* perform session switch */
  session_switch(session_type);
  if (session_type == SESSION_PROGRAMMING)
  {
    board_5v_set(0U);
  }

  /* update tester present tick on session control */
  last_tester_present_tick = timer_get_tick();

  /* send positive response unless suppressed */
  if (!suppress)
  {
    uint16_t p2star_units;
    resp[0] = UDS_SID_DIAG_SESSION_CTRL + UDS_POSITIVE_RESPONSE_OFFSET;
    resp[1] = session_type;
    /* ISO 14229 sessionParameterRecord: P2 (1ms), P2* (10ms). Do not piggyback
     * LP wakeup flags here — CCU treats bytes 2..5 as timing and P2*=0
     * makes 7F xx 78 expire in ~5s with no retry. */
    resp[2] = (uint8_t)((UDS_P2_TIMEOUT_MS >> 8) & 0xFFU);
    resp[3] = (uint8_t)(UDS_P2_TIMEOUT_MS & 0xFFU);
    p2star_units = (uint16_t)(UDS_P2_STAR_TIMEOUT_MS / 10U);
    resp[4] = (uint8_t)((p2star_units >> 8) & 0xFFU);
    resp[5] = (uint8_t)(p2star_units & 0xFFU);
    proto_send_response(resp, 6U);
    if (session_type == SESSION_DEFAULT)
    {
      (void)can_driver_wait_tx_idle(20U);
      can_lp_send_ident();
    }
  }
}

/**
 * @brief  ReadDataByIdentifier (0x22)
 * @param  data: UDS payload
 * @param  len:  payload length
 * @retval none
 */
static void handle_read_data_by_id(uint8_t *data, uint16_t len)
{
  uint8_t resp[256];
  uint16_t pos;
  uint16_t i;

  if ((len < 3U) || (((len - 1U) % 2U) != 0U))
  {
    proto_send_nrc(UDS_SID_READ_DATA_BY_ID, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
    return;
  }

  /* DID 0x2013 主动问询：UART 往返延迟走延迟应答（先 7F 22 78，Qi 回复后
   * 62 20 13 + ASCII "QC_JYF_MCU2_FW_1.1.X"，变长 23~27B），不进同步 fill 路径。
   * 仅支持单独读；组合读回 NRC 0x22（延迟应答无法服务多 DID）。 */
  if ((len == 3U) &&
      ((((uint16_t)data[1] << 8) | (uint16_t)data[2]) == DID_QI_VERSION_QUERY))
  {
    if ((g_qi_ver_q_state != 0U) || (g_qi_iap_state != QI_IAP_IDLE))
    {
      /* 查询进行中不排队；Qi IAP 升级中避免 UART 命令交叉 */
      proto_send_nrc(UDS_SID_READ_DATA_BY_ID, UDS_NRC_CONDITIONS_NOT_CORRECT);
      return;
    }
    g_qi_ver_q_state    = 1U;
    g_qi_ver_q_start_ms = timer_get_tick();
    proto_send_pending(UDS_SID_READ_DATA_BY_ID);
    (void)qi_protocol_send(QI_CMD_VERSION_QUERY, (const uint8_t *)0, 0U, 1U);
    return;
  }

  /* DID 0x2140 Qi UART 抓取读取：应答变长（flags+len+最多 240B 数据，
   * 总长可达 245B）且 siphon 语义每次读消耗缓冲，不进同步 fill 路径
   * （fill 载荷缓冲仅 32B）。仅支持单独读；组合读含 0x2140 回 NRC 0x22
   * （对齐 0x2013 组合读拒绝口径）。任意会话、无需安全访问。 */
  if ((len == 3U) &&
      ((((uint16_t)data[1] << 8) | (uint16_t)data[2]) == DID_QI_UART_SNIFF))
  {
    uint8_t flags = 0U;
    uint16_t n;

    resp[0] = UDS_SID_READ_DATA_BY_ID + UDS_POSITIVE_RESPONSE_OFFSET;
    resp[1] = data[1];
    resp[2] = data[2];
    /* siphon：只清已返回部分，缓冲多于 240B 时下次续读；空回 [00][00] */
    n = qi_sniff_read(&resp[5], 240U, &flags);
    resp[3] = flags;              /* bit0=自上次读以来发生过溢出丢弃，其余 0 */
    resp[4] = (uint8_t)n;
    proto_send_response(resp, (uint16_t)(5U + n));
    return;
  }

  resp[0] = UDS_SID_READ_DATA_BY_ID + UDS_POSITIVE_RESPONSE_OFFSET;
  pos = 1U;
  for (i = 1U; i < len; i += 2U)
  {
    uint16_t did = ((uint16_t)data[i] << 8) | (uint16_t)data[i + 1U];
    uint8_t payload[32];
    uint8_t plen = 0U;

    if (did == DID_QI_VERSION_QUERY)
    {
      /* 组合读含 0x2013：延迟应答无法服务 → NRC 0x22 */
      proto_send_nrc(UDS_SID_READ_DATA_BY_ID, UDS_NRC_CONDITIONS_NOT_CORRECT);
      return;
    }

    if (did == DID_QI_UART_SNIFF)
    {
      /* 组合读含 0x2140：变长应答 + siphon 消耗语义无法服务 → NRC 0x22 */
      proto_send_nrc(UDS_SID_READ_DATA_BY_ID, UDS_NRC_CONDITIONS_NOT_CORRECT);
      return;
    }

    if (fill_did_payload(did, payload, &plen) != 0)
    {
      proto_send_nrc(UDS_SID_READ_DATA_BY_ID, UDS_NRC_REQUEST_OUT_OF_RANGE);
      return;
    }
    if ((pos + 2U + plen) > sizeof(resp))
    {
      proto_send_nrc(UDS_SID_READ_DATA_BY_ID, UDS_NRC_RESPONSE_TOO_LONG);
      return;
    }
    resp[pos++] = data[i];
    resp[pos++] = data[i + 1U];
    memcpy(&resp[pos], payload, plen);
    pos = (uint16_t)(pos + plen);
  }
  proto_send_response(resp, pos);
}

/**
 * @brief  WriteDataByIdentifier (0x2E)
 * @note   逐 DID 门禁（本文件 WDBI case 表，同值 NRC 判读以 case 块内
 *         检查顺序为准）：0x2101/0x210D 需 EXTENDED 会话（10 03）；
 *         0x2010/0xF18C/0x2120/0x2130/0x2131 需 PROGRAMMING 会话
 *         （10 02）；全部可写 DID 均需 SecurityAccess 解锁；其他 DID
 *         回 NRC 0x31。同一 case 内会话检查先于安全检查（NRC 0x22 在
 *         0x33 之前——写步拿 0x33=会话存活证据，拿 0x22 才是会话丢失）。
 * @param  data: UDS payload
 * @param  len:  payload length
 * @retval none
 */
static void handle_write_data_by_id(uint8_t *data, uint16_t len)
{
  uint8_t resp[4];
  uint16_t did;

  /* minimum: SID + DID_H + DID_L + 1 byte data */
  if (len < 4U)
  {
    proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
    return;
  }

  did = ((uint16_t)data[1] << 8) | (uint16_t)data[2];

  /* check session & security per DID */
  switch (did)
  {
    /* Extended session + SA DIDs (Qi charger config) */
    case DID_CHARGER_ENABLE:
    case DID_POWER_LIMIT:
      if (current_session != SESSION_EXTENDED)
      {
        proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_CONDITIONS_NOT_CORRECT);
        return;
      }
      if (!security_unlocked)
      {
        proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_SECURITY_ACCESS_DENIED);
        return;
      }
      break;

    /* Programming session + SA DIDs */
    case DID_FW_TYPE:
    case DID_SERIAL_NUMBER:
    case DID_ECDSA_PUBKEY:
    case DID_QI_IAP_CONTROL:
    case DID_QI_IAP_DATA:
    case DID_QI_UART_TX:
      if (current_session != SESSION_PROGRAMMING)
      {
        proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_CONDITIONS_NOT_CORRECT);
        return;
      }
      if (!security_unlocked)
      {
        proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_SECURITY_ACCESS_DENIED);
        return;
      }
      break;

    default:
      proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_REQUEST_OUT_OF_RANGE);
      return;
  }

  {
    switch (did)
    {
      case DID_FW_TYPE:
      {
        uint8_t fw_type = data[3];
        if ((fw_type < FW_TYPE_APP) || (fw_type > FW_TYPE_BOOTLOADER))
        {
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_REQUEST_OUT_OF_RANGE);
          return;
        }
        resp[0] = UDS_SID_WRITE_DATA_BY_ID + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        proto_send_response(resp, 3);
        break;
      }

      case DID_SERIAL_NUMBER:
      {
        uint8_t sn32[32];
        uint16_t n;
        uint16_t i;

        n = (uint16_t)(len - 3U);
        if (n > 32U)
        {
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_REQUEST_OUT_OF_RANGE);
          return;
        }
        memset(sn32, 0x20, 32U);
        for (i = 0U; i < n; i++)
        {
          sn32[i] = data[3U + i];
        }
        if (device_info_write_sn(sn32) != 0)
        {
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
          return;
        }
        (void)sit1145_normal_mode_set();
        (void)can_driver_wait_tx_idle(50U);
        resp[0] = UDS_SID_WRITE_DATA_BY_ID + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        proto_send_response(resp, 3);
        (void)can_driver_wait_tx_idle(50U);
        break;
      }

      case DID_ECDSA_PUBKEY:
      {
        uint16_t n;

        n = (uint16_t)(len - 3U);
        if (n != 65U)
        {
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
          return;
        }
        if (data[3] != 0x04U)
        {
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_REQUEST_OUT_OF_RANGE);
          return;
        }
        if (device_info_write_pubkey(&data[3]) != 0)
        {
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
          return;
        }
        resp[0] = UDS_SID_WRITE_DATA_BY_ID + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        proto_send_response(resp, 3);
        break;
      }

      case DID_QI_IAP_CONTROL:
      {
        /* data[3]=0x01 启动（data[4..5]=固件大小 16-bit BE）
         * data[3]=0x00/0x02 中止。UART：0xCC 0x01 + size */
        uint8_t sub = data[3];
        if (sub == 0x01U)
        {
          if (len < 6U)
          {
            proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
            return;
          }
          if (g_qi_ver_q_state != 0U)
          {
            /* 版本问询进行中：避免 UART 命令交叉 */
            proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_CONDITIONS_NOT_CORRECT);
            return;
          }
          g_qi_iap_total    = ((uint16_t)data[4] << 8) | (uint16_t)data[5];
          g_qi_iap_sent     = 0U;
          g_qi_iap_progress = 0U;
          g_qi_iap_last_tx_ms = 0U;
          g_qi_iap_pending_chunk = 0U;
          /* Discard leftover 0x01 reports so the prepare ACK can be parsed. */
          qi_protocol_rx_flush();
          (void)qi_protocol_iap_prepare(g_qi_iap_total);
          /* Wait for Qi prepare ACK (chip may erase flash) before 6E 21 30. */
          g_qi_iap_state = QI_IAP_WAIT_ACK;
          g_qi_iap_ack_timeout_ms = QI_IAP_PREPARE_TIMEOUT_MS;
          g_qi_iap_wait_start_ms = timer_get_tick();
          g_qi_iap_pending_did[0] = data[1];
          g_qi_iap_pending_did[1] = data[2];
        }
        else if ((sub == 0x00U) || (sub == 0x02U))
        {
          g_qi_iap_state = QI_IAP_IDLE;
          g_qi_iap_progress = 0U;
          g_qi_iap_total = 0U;
          g_qi_iap_sent = 0U;
          g_qi_iap_last_tx_ms = 0U;
          g_qi_iap_pending_chunk = 0U;
          g_qi_iap_pending_did[0] = 0U;
          g_qi_iap_pending_did[1] = 0U;
          resp[0] = UDS_SID_WRITE_DATA_BY_ID + UDS_POSITIVE_RESPONSE_OFFSET;
          resp[1] = data[1];
          resp[2] = data[2];
          proto_send_response(resp, 3);
        }
        else
        {
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_REQUEST_OUT_OF_RANGE);
        }
        break;
      }

      case DID_QI_IAP_DATA:
      {
        /* data[3..4]=地址 16-bit BE，data[5..]=固件（最多 22B）
         * UART：0xCC 0x02 + addr + data
         *
         * ACK 链路：发 UART 帧 → 等 Qi 芯片 ACK → 才回 UDS 正响应。
         * 非阻塞：设 WAIT_ACK 状态，UDS 响应在 qi_iap_ack_poll() 中延迟发送。
         * Host 侧收到 NRC 0x72 时重试当前包。 */
        uint16_t addr;
        uint16_t chunk_len;

        if (len < 6U)
        {
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
          return;
        }
        if (g_qi_iap_state != QI_IAP_IN_PROGRESS)
        {
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_CONDITIONS_NOT_CORRECT);
          return;
        }
        addr = ((uint16_t)data[3] << 8) | (uint16_t)data[4];
        chunk_len = (uint16_t)(len - 5U);
        if (chunk_len > QI_IAP_MAX_CHUNK)
        {
          chunk_len = QI_IAP_MAX_CHUNK;
        }
        (void)qi_protocol_iap_data(addr, &data[5], (uint8_t)chunk_len);
        g_qi_iap_pending_chunk = (uint16_t)chunk_len;
        g_qi_iap_last_tx_ms = timer_get_tick();
        /* 不立即回 UDS 响应——进入 WAIT_ACK 状态，
         * 在 qi_iap_ack_poll() 中等 Qi 芯片 ACK 后再回复 */
        g_qi_iap_state = QI_IAP_WAIT_ACK;
        g_qi_iap_ack_timeout_ms = QI_IAP_ACK_TIMEOUT_MS;
        g_qi_iap_wait_start_ms = timer_get_tick();
        g_qi_iap_pending_did[0] = data[1];
        g_qi_iap_pending_did[1] = data[2];
        break;
      }

      case DID_QI_UART_TX:
      {
        /* Qi UART 透传发送：payload 1~64B 原样逐字节发给 Qi 芯片。
         * 会话+安全门禁已在上方 case 分组检过（同 0x2130/0x2131）；
         * 这里只做长度校验 + Qi IAP 进行中拒绝（避免 UART 命令交叉，
         * NRC 对齐 0x2130 拒绝条件）。 */
        uint16_t n = (uint16_t)(len - 3U);

        if ((n < 1U) || (n > 64U))
        {
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
          return;
        }
        if (g_qi_iap_state != QI_IAP_IDLE)
        {
          /* Qi IAP 升级中：避免 UART 命令交叉 */
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_CONDITIONS_NOT_CORRECT);
          return;
        }
        qi_uart_send(&data[3], (uint8_t)n);
        resp[0] = UDS_SID_WRITE_DATA_BY_ID + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        proto_send_response(resp, 3);
        break;
      }

      case DID_CHARGER_ENABLE:
      {
        uint8_t val = data[3];
        if (val > 0x01U)
        {
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_REQUEST_OUT_OF_RANGE);
          return;
        }
        /* reject enable (0x01) when blocking fault active */
        if ((val == 0x01U) && (g_qi_fault_code != 0x00U))
        {
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_CONDITIONS_NOT_CORRECT);
          return;
        }
        g_qi_charger_enable = val;
        board_charge_set_enable(val);  /* CCU enable; PB2 controlled by charge_poll */
        resp[0] = UDS_SID_WRITE_DATA_BY_ID + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        proto_send_response(resp, 3);
        break;
      }

      case DID_POWER_LIMIT:
      {
        uint16_t val;
        uint8_t nvm_buf[2];
        if (len < 5U)
        {
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
          return;
        }
        val = (uint16_t)data[3] | ((uint16_t)data[4] << 8);
        /* only accept 5W / 10W / 15W */
        if ((val != 500U) && (val != 1000U) && (val != 1500U))
        {
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_REQUEST_OUT_OF_RANGE);
          return;
        }
        g_qi_power_limit_mw = val;
        nvm_buf[0] = (uint8_t)(val & 0xFFU);
        nvm_buf[1] = (uint8_t)((val >> 8) & 0xFFU);
        (void)qi_nvm_save(NVM_OFFSET_POWER_LIMIT, nvm_buf, 2U);
        /* forward to Qi chip: 5W=0x01, 10W=0x02, 15W=0x03 */
        {
          uint8_t qi_power = (val == 500U) ? QI_POWER_5W :
                             (val == 1000U) ? QI_POWER_10W : QI_POWER_15W;
          (void)qi_protocol_set_power(qi_power, 0U);
        }
        resp[0] = UDS_SID_WRITE_DATA_BY_ID + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        proto_send_response(resp, 3);
        break;
      }

      default:
        /* DID not writable */
        proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_REQUEST_OUT_OF_RANGE);
        break;
    }
  }
}

/**
 * @brief  SecurityAccess (0x27)
 * @note   APP side implements SecurityAccess fully (Boot safe mode does not):
 *         27 01 -> 67 01 + 32-byte seed (g_seed[32], refreshed on every 27 01,
 *         signature buffer cleared with it); unlocked 27 01 -> 67 01 + 32x0x00.
 *         27 03 -> chunked signature transfer (4B/frame x 16, blockSeq 0x01..0x10,
 *         blockSeq 0x01 resets the buffer); 27 02 -> sha256_hash(g_seed, 32U) +
 *         uECC_verify on the accumulated 64-byte signature.
 *         Verify fail: NRC 0x35 (invalidKey, fail_count+1); fail_count >= 3 arms
 *         the ~30s lockout: 27 02 -> NRC 0x36, 27 01 inside lockout -> NRC 0x37.
 * @param  data: UDS payload
 * @param  len:  payload length
 * @retval none
 */
static void handle_security_access(uint8_t *data, uint16_t len)
{
  uint8_t resp[8];
  uint8_t sub_func;
  uint32_t now_ms;

  if (len < 2U)
  {
    proto_send_nrc(UDS_SID_SECURITY_ACCESS, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
    return;
  }

  sub_func = data[1] & UDS_SUBFUNC_MASK;
  now_ms = timer_get_tick();

  if (sub_func == 0x01U)
  {
    if (g_security_fail_count >= SECURITY_MAX_FAILURES)
    {
      if ((int32_t)(now_ms - g_security_lockout_until_ms) < 0)
      {
        proto_send_nrc(UDS_SID_SECURITY_ACCESS, UDS_NRC_REQUIRED_TIME_DELAY);
        return;
      }
      g_security_fail_count = 0;
    }
    if (security_unlocked)
    {
      {
        uint8_t unlock_resp[34];
        unlock_resp[0] = UDS_SID_SECURITY_ACCESS + UDS_POSITIVE_RESPONSE_OFFSET;
        unlock_resp[1] = 0x01U;
        memset(&unlock_resp[2], 0, 32U);
        proto_send_response(unlock_resp, 34);
      }
      return;
    }
    {
      uint8_t idx;
      for (idx = 0U; idx < 32U; idx += 4U)
      {
        uint32_t seed_val = generate_random_seed();
        g_seed[idx]     = (uint8_t)((seed_val >> 24) & 0xFFU);
        g_seed[idx + 1] = (uint8_t)((seed_val >> 16) & 0xFFU);
        g_seed[idx + 2] = (uint8_t)((seed_val >> 8) & 0xFFU);
        g_seed[idx + 3] = (uint8_t)(seed_val & 0xFFU);
      }
    }
    g_seed_generated = 1;
    g_seed_sub = 0x01U;
    g_sa_sig_bytes_received = 0;
    g_sa_sig_block_seq = 0;
    {
      uint8_t sa_resp[34];
      sa_resp[0] = UDS_SID_SECURITY_ACCESS + UDS_POSITIVE_RESPONSE_OFFSET;
      sa_resp[1] = 0x01U;
      memcpy(&sa_resp[2], g_seed, 32U);
      proto_send_response(sa_resp, 34);
    }
  }
  else if (sub_func == 0x03U)
  {
    uint8_t block_seq;
    uint8_t chunk_len;
    uint8_t i;

    if (!g_seed_generated)
    {
      proto_send_nrc(UDS_SID_SECURITY_ACCESS, UDS_NRC_REQUEST_SEQUENCE_ERROR);
      return;
    }
    if (len < 3U)
    {
      proto_send_nrc(UDS_SID_SECURITY_ACCESS, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
      return;
    }
    block_seq = data[2];
    if (block_seq == 0x01U)
    {
      g_sa_sig_block_seq = 0;
      g_sa_sig_bytes_received = 0;
      memset(g_sa_sig_buf, 0, 64);
    }
    g_sa_sig_block_seq++;
    if (g_sa_sig_block_seq == 0x00U)
    {
      g_sa_sig_block_seq = 0x01U;
    }
    if (block_seq != g_sa_sig_block_seq)
    {
      g_sa_sig_bytes_received = 0;
      proto_send_nrc(UDS_SID_SECURITY_ACCESS, UDS_NRC_TRANSFER_DATA_SUSPENDED);
      return;
    }
    chunk_len = (uint8_t)(len - 3U);
    if ((g_sa_sig_bytes_received + chunk_len) > 64U)
    {
      chunk_len = (uint8_t)(64U - g_sa_sig_bytes_received);
    }
    for (i = 0U; i < chunk_len; i++)
    {
      g_sa_sig_buf[g_sa_sig_bytes_received + i] = data[3U + i];
    }
    g_sa_sig_bytes_received = (uint8_t)(g_sa_sig_bytes_received + chunk_len);
    resp[0] = UDS_SID_SECURITY_ACCESS + UDS_POSITIVE_RESPONSE_OFFSET;
    resp[1] = 0x03U;
    resp[2] = block_seq;
    proto_send_response(resp, 3);
  }
  else if (sub_func == 0x02U)
  {
    uint8_t hash[32];

    if (!g_seed_generated)
    {
      proto_send_nrc(UDS_SID_SECURITY_ACCESS, UDS_NRC_REQUEST_SEQUENCE_ERROR);
      return;
    }
    /* 27 02 + 64B key in one ISO-TP message, or 27 03 chunks already in buf.
     * Do not clear seed on short 27 02 — host may retry 27 03 / 27 02. */
    if ((g_sa_sig_bytes_received != 64U) && (len >= 66U))
    {
      uint16_t k;
      for (k = 0U; k < 64U; k++)
      {
        g_sa_sig_buf[k] = data[2U + k];
      }
      g_sa_sig_bytes_received = 64U;
    }
    if (g_sa_sig_bytes_received != 64U)
    {
      proto_send_nrc(UDS_SID_SECURITY_ACCESS, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
      return;
    }
    proto_begin_long_op(UDS_SID_SECURITY_ACCESS);
    sha256_hash(g_seed, 32U, hash);
    if (uECC_verify(g_app_ecdsa_pubkey, hash, g_sa_sig_buf) == 1)
    {
      security_unlocked = 1;
      g_security_fail_count = 0;
      g_seed_generated = 0;
      proto_end_long_op();
      resp[0] = UDS_SID_SECURITY_ACCESS + UDS_POSITIVE_RESPONSE_OFFSET;
      resp[1] = 0x02U;
      proto_send_response(resp, 2);
    }
    else
    {
      security_unlocked = 0;
      g_security_fail_count++;
      g_seed_generated = 0;
      g_sa_sig_bytes_received = 0;
      proto_end_long_op();
      if (g_security_fail_count >= SECURITY_MAX_FAILURES)
      {
        g_security_lockout_until_ms = timer_get_tick() + SECURITY_LOCKOUT_MS;
        proto_send_nrc(UDS_SID_SECURITY_ACCESS, UDS_NRC_EXCEEDED_NUMBER_OF_ATTEMPTS);
      }
      else
      {
        proto_send_nrc(UDS_SID_SECURITY_ACCESS, UDS_NRC_INVALID_KEY);
      }
    }
  }
  else
  {
    proto_send_nrc(UDS_SID_SECURITY_ACCESS, UDS_NRC_SUBFUNCTION_NOT_SUPPORTED);
  }
}

/**
 * @brief  RoutineControl (0x31)
 * @note   按 rid 分发：
 *         - 0x2100 Clear Faults：默认会话即可执行，不检查 Programming 会话 /
 *           SecurityAccess（Qi 侧要求，门禁过严会回 NRC 0x22 导致协议不通）；
 *           StartRoutine 执行逻辑暂留空（TODO），正响应 71 01 21 00 00；
 *           StopRoutine/RequestRoutineResults 暂不支持，回 NRC 0x31。
 *         - 其余 rid（含 0xFF00）：原样转 ota_dl_handle_erase()，APP 实现
 *           槽擦写全链（ota_download.c）：0x31 擦除非活跃槽 → 0x34/0x36 下载
 *           编程 → 0x37 验签+commit_backup 后自复位，BOOT 搬运至 App 区；
 *           门禁=PROGRAMMING 会话+SecurityAccess（handler 内逐项检查）。
 *           Boot 仅在复位后按 trial metadata 选槽/回滚/进 safe mode，
 *           不承担下载编程（旧"Boot safe mode only"架构已废弃）。
 */
static void handle_routine_control(uint8_t *data, uint16_t len)
{
  uint8_t  resp[5];
  uint8_t  sub_func;
  uint8_t  suppress;
  uint16_t rid;

  /* 长度检查先于 rid 分发：len<4 的畸形请求统一回 NRC 0x13 */
  if (len < 4U)
  {
    proto_send_nrc(UDS_SID_ROUTINE_CONTROL, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
    return;
  }

  sub_func = data[1] & UDS_SUBFUNC_MASK;
  suppress = data[1] & UDS_SUBFUNC_SUPPRESS_POS_RESP;
  rid      = ((uint16_t)data[2] << 8) | (uint16_t)data[3];

  if (rid == ROUTINE_CLEAR_FAULTS)
  {
    /* Clear Faults 分支：仅支持 StartRoutine(0x01)。
     * suppressPosResp 只抑制正响应，NRC 仍须发送（ISO 14229-1）。
     * 本分支刻意不检查 Programming 会话/SecurityAccess：
     * 默认会话必须可执行，否则又回 0x22，协议不通。 */
    if (sub_func != 0x01U)
    {
      proto_send_nrc(UDS_SID_ROUTINE_CONTROL, UDS_NRC_REQUEST_OUT_OF_RANGE);
      return;
    }

    /* TODO: 待定义 Qi 侧故障标志/寄存器清除动作（Qi 侧故障标志清单待确认） */

    if (!suppress)
    {
      resp[0] = UDS_SID_ROUTINE_CONTROL + UDS_POSITIVE_RESPONSE_OFFSET; /* 0x71 */
      resp[1] = 0x01U;                  /* StartRoutine 回显（suppress 位已剥除） */
      resp[2] = (uint8_t)(rid >> 8);    /* 0x21 */
      resp[3] = (uint8_t)(rid & 0xFFU); /* 0x00 */
      resp[4] = 0x00U;                  /* RoutineStatusRecord: 0x00 = 成功 */
      proto_send_response(resp, 5U);    /* 5 字节 ≤ 7，单帧直发，无流控/多帧 */
    }
    return;
  }

  /* 其余 rid（含 0xFF00 erase）：保持原行为，data/len 原样转发，
   * 门禁逻辑一字不改，仍在 ota_dl_handle_erase 内逐项检查 */
  ota_dl_handle_erase(data, len);
}

/**
 * @brief  TesterPresent (0x3E)
 * @param  data: UDS payload
 * @param  len:  payload length
 * @retval none
 */
static void handle_tester_present(uint8_t *data, uint16_t len)
{
  uint8_t resp[8];
  uint8_t sub_func;
  uint8_t suppress;

  /* update session keepalive timestamp */
  last_tester_present_tick = timer_get_tick();

  if (len >= 2U)
  {
    sub_func = data[1];
    suppress = sub_func & UDS_SUBFUNC_SUPPRESS_POS_RESP;

    if (!suppress)
    {
      resp[0] = UDS_SID_TESTER_KEEPALIVE + UDS_POSITIVE_RESPONSE_OFFSET;
      resp[1] = sub_func & UDS_SUBFUNC_MASK;
      proto_send_response(resp, 2);
    }
  }
  else
  {
    /* no sub-function: send positive response */
    resp[0] = UDS_SID_TESTER_KEEPALIVE + UDS_POSITIVE_RESPONSE_OFFSET;
    proto_send_response(resp, 1);
  }
}

/* ========================================================================== */
/*  Qi IAP frame callback                                                    */
/* ========================================================================== */

/** @brief  Qi IAP ACK status codes from Qi chip */
#define QI_IAP_ACK_OK       0x00U
#define QI_IAP_ACK_COMPLETE 0x02U
#define QI_IAP_ACK_FAILED   0x03U

/**
 * @brief  apply a parsed Qi IAP ACK status to the state machine
 */
static void qi_iap_apply_ack_status(uint8_t status)
{
  if (status == QI_IAP_ACK_FAILED)
  {
    g_qi_iap_state = QI_IAP_FAILED;
    g_qi_iap_pending_chunk = 0U;
    return;
  }

  if (status == QI_IAP_ACK_COMPLETE)
  {
    if (g_qi_iap_pending_chunk > 0U)
    {
      g_qi_iap_sent += g_qi_iap_pending_chunk;
      g_qi_iap_pending_chunk = 0U;
    }
    g_qi_iap_state = QI_IAP_SUCCESS;
    g_qi_iap_progress = 100U;
    return;
  }

  if (status == QI_IAP_ACK_OK)
  {
    if (g_qi_iap_state == QI_IAP_WAIT_ACK)
    {
      if (g_qi_iap_pending_chunk > 0U)
      {
        g_qi_iap_sent += g_qi_iap_pending_chunk;
        g_qi_iap_pending_chunk = 0U;
        if (g_qi_iap_total > 0U)
        {
          g_qi_iap_progress = (uint8_t)((uint32_t)g_qi_iap_sent * 100U / g_qi_iap_total);
          if (g_qi_iap_progress > 100U)
          {
            g_qi_iap_progress = 100U;
          }
        }
      }
      g_qi_iap_state = QI_IAP_IN_PROGRESS;
    }
  }
}

/**
 * @brief  Qi frame callback: handle IAP ACK and status report (0x01)
 */
static void qi_iap_frame_cb(const qi_frame_t *frame)
{
  if (frame == (const qi_frame_t *)0)
  {
    return;
  }

  /* ---- Qi IAP ACK ----
   * 0xCC: data[0]=sub_cmd (0x01/0x02), data[1]=status; reserved optional
   * 0x00: generic ACK, data[0]=status
   * Some chips omit the reserved 0x00, so accept data_len >= 1. */
  if (frame->cmd == QI_CMD_IAP)
  {
    uint8_t status;

    if (frame->data_len < 1U)
    {
      return;
    }
    if ((frame->data_len >= 2U) &&
        ((frame->data[0] == QI_IAP_PREPARE) || (frame->data[0] == QI_IAP_DATA)))
    {
      status = frame->data[1];
    }
    else
    {
      status = frame->data[0];
    }
    qi_iap_apply_ack_status(status);
    return;
  }

  if ((frame->cmd == QI_CMD_ACK) && (g_qi_iap_state == QI_IAP_WAIT_ACK))
  {
    if (frame->data_len >= 1U)
    {
      qi_iap_apply_ack_status(frame->data[0]);
    }
    return;
  }

  /* ---- Qi 版本查询回复（DID 0x2013 主动问询）----
   * 主格式：CMD 0x03 + 版本（整数，规格 2B LE，宽松接受 data_len>=1）；
   * 兼容：Qi 侧用通用 ACK(0x00) 携带版本（仅在 IAP 空闲时采纳，避免误吞
   * IAP ACK）。按整数稳健化解析（qi_ver_parse_value：ASCII 十进制数字串
   * 或小端整数），缓存完整整数值并置有效标志，并回
   * 62 20 13 + ASCII "QC_JYF_MCU2_FW_1.1.X"（X=十进制数字，变长 23~27B，
   * proto_send_response 自动走 g_tx_pend 多帧延迟路径，帧回调内安全）；
   * 超时兜底在 qi_ver_query_poll()。 */
  if (g_qi_ver_q_state == 1U)
  {
    const uint8_t *vsrc = (const uint8_t *)0;
    uint8_t        vlen = 0U;

    if ((frame->cmd == QI_CMD_VERSION_QUERY) && (frame->data_len >= 1U))
    {
      vsrc = &frame->data[0];      /* 问询专用回复：数据区即版本 */
      vlen = frame->data_len;
    }
    else if ((frame->cmd == QI_CMD_ACK) && (g_qi_iap_state == QI_IAP_IDLE) &&
             (frame->data_len >= 2U))
    {
      vsrc = &frame->data[0];      /* 通用 ACK 须 2B 形状，防误吞 1B 状态 ACK */
      vlen = frame->data_len;      /* ACK 帧携带 2B 版本 */
    }
    if (vsrc != (const uint8_t *)0)
    {
      g_qi_fw_version       = qi_ver_parse_value(vsrc, vlen);
      g_qi_fw_version_valid = 1U;
      g_qi_ver_q_state      = 0U;
      qi_ver_send_full_response(g_qi_fw_version);
      return;
    }
  }

  /* ---- Qi status report (0x01) 信息读取 ----
   * 规范 6 字节：status1, status2, power LE, version LE
   * 扩展 ≥13 字节：额外电压/电流/温度/FOD/故障/降额 */
  if (frame->cmd == QI_CMD_STATUS_REPORT)
  {
    uint8_t status_byte;

    if (frame->data_len < 6U)
    {
      return;
    }

    status_byte = frame->data[0];

    /* decode charge state from status bits */
    if ((status_byte & QI_STATUS_CHARGING) != 0U)
    {
      g_qi_charge_state = QI_CHARGE_CHARGING;
      g_qi_device_present = 1U;
    }
    else if ((status_byte & QI_STATUS_FULL) != 0U)
    {
      g_qi_charge_state = QI_CHARGE_COMPLETE;
      g_qi_device_present = 1U;
    }
    else if ((status_byte & QI_STATUS_PING) != 0U)
    {
      g_qi_charge_state = QI_CHARGE_DEVICE_DETECTED;
      g_qi_device_present = 1U;
    }
    else
    {
      /* no device-related status bits set */
      if (g_qi_charger_enable != 0U)
      {
        g_qi_charge_state = QI_CHARGE_STANDBY;
      }
      else
      {
        g_qi_charge_state = QI_CHARGE_DISABLED;
      }
      g_qi_device_present = 0U;
    }

    /* protection/fault bits */
    if ((status_byte & QI_STATUS_FOD) != 0U)
    {
      g_qi_fod_status = 0x02U;  /* confirmed */
      g_qi_fault_code = 0x06U;  /* FOD fault */
      if (g_qi_charge_state == QI_CHARGE_CHARGING)
      {
        g_qi_charge_state = QI_CHARGE_SUSPENDED_FOD;
      }
    }
    else if ((status_byte & QI_STATUS_OTP) != 0U)
    {
      g_qi_fault_code = 0x07U;  /* coil over-temp */
      if (g_qi_charge_state == QI_CHARGE_CHARGING)
      {
        g_qi_charge_state = QI_CHARGE_SUSPENDED_THERMAL;
      }
    }
    else if ((status_byte & (QI_STATUS_OVP | QI_STATUS_UVP | QI_STATUS_OCP)) != 0U)
    {
      if ((status_byte & QI_STATUS_OVP) != 0U)
      {
        g_qi_fault_code = 0x04U;  /* input over-voltage */
      }
      else if ((status_byte & QI_STATUS_UVP) != 0U)
      {
        g_qi_fault_code = 0x05U;  /* input under-voltage */
      }
      else
      {
        g_qi_fault_code = 0x0AU;  /* input over-current */
      }
      g_qi_charge_state = QI_CHARGE_FAULT;
    }

    /* 帧布局（docs/4. IAP数据通信协议规范.md §2.1，0 基帧偏移）：
     *   data[0-1] = status1/status2（帧偏移 4-5）
     *   data[2-3] = 实时功率 LE mW（帧偏移 6-7）
     *   data[4-5] = 版本号 LE（帧偏移 8-9，与 DID 0x2133 一致）
     * 扩展帧 (≥13B) 额外字段：
     *   data[6]   = voltage, data[7] = current, data[8] = temp
     *   data[9]   = FOD, data[10] = reserved, data[11] = fault
     *   data[12]  = thermal derate
     *
     * NOTE: 曾按 data[2-3]=version/data[4-5]=power 解析（与规格书
     *       装反），2026-09-22 对齐规格书修正。 */
    g_qi_output_power_mw = (uint16_t)frame->data[2]
                         | ((uint16_t)frame->data[3] << 8);
    g_qi_fw_version = (uint16_t)frame->data[4]
                    | ((uint16_t)frame->data[5] << 8);
    g_qi_fw_version_valid = 1U;

    /* 扩展帧额外字段 */
    if (frame->data_len >= 13U)
    {
      g_qi_voltage_raw = frame->data[6];
      g_qi_current_raw = frame->data[7];
      g_qi_pcb_temp = frame->data[8];
      if (frame->data[9] != 0x00U)
      {
        g_qi_fod_status = frame->data[9];
      }
      if (frame->data[11] != 0x00U)
      {
        g_qi_fault_code = frame->data[11];
      }
      g_qi_thermal_derate = frame->data[12];
    }

    return;
  }
}

/* ========================================================================== */
/*  Main UDS message dispatcher                                              */
/* ========================================================================== */

/**
 * @brief  process a complete UDS message (ISO-TP payload already extracted)
 * @note   called from isotp_message_received() after reassembly.
 * @param  data: pointer to UDS payload (first byte is SID)
 * @param  len:  UDS payload length
 * @retval none
 */
static void uds_process_message(uint8_t *data, uint16_t len)
{
  uint8_t service_id;

  if ((data == (uint8_t *)0) || (len == 0U))
  {
    return;
  }

  /* any diagnostic request refreshes S3 (ISO 14229) and the 6 min bus idle */
  last_tester_present_tick = timer_get_tick();
  can_lp_mark_uds();

  service_id = data[0];

  switch (service_id)
  {
    case UDS_SID_DIAG_SESSION_CTRL:
      handle_diag_session_ctrl(data, len);
      break;

    case UDS_SID_READ_DATA_BY_ID:
      handle_read_data_by_id(data, len);
      break;

    case UDS_SID_WRITE_DATA_BY_ID:
      handle_write_data_by_id(data, len);
      break;

    case UDS_SID_SECURITY_ACCESS:
      handle_security_access(data, len);
      break;

    case UDS_SID_ROUTINE_CONTROL:
      handle_routine_control(data, len);
      break;

    case UDS_SID_REQUEST_DOWNLOAD:
      ota_dl_handle_request_download(data, len);
      break;

    case UDS_SID_TRANSFER_DATA:
      ota_dl_handle_transfer_data(data, len);
      break;

    case UDS_SID_TRANSFER_EXIT:
      ota_dl_handle_transfer_exit(data, len);
      break;

    case UDS_SID_TRANSFER_SIGNATURE:
      proto_send_nrc(service_id, UDS_NRC_SERVICE_NOT_SUPPORTED);
      break;

    case UDS_SID_TESTER_KEEPALIVE:
      handle_tester_present(data, len);
      break;

    default:
      /* unsupported service */
      proto_send_nrc(service_id, UDS_NRC_SERVICE_NOT_SUPPORTED);
      break;
  }
}

/* ========================================================================== */
/*  ISO-TP callback and CAN RX handler                                       */
/* ========================================================================== */

/**
 * @brief  ISO-TP completion callback
 * @note   invoked when a complete UDS message has been reassembled.
 *         checks session timeout before forwarding to uds_process_message().
 * @param  data: pointer to complete UDS payload
 * @param  len:  payload length in bytes
 * @retval none
 */
static void isotp_message_received(uint8_t *data, uint16_t len)
{
  uint32_t now;

  /* session timeout check: if in non-default session and no TesterPresent
   * received within SESSION_TIMEOUT_MS, fall back to default session */
  if (current_session != SESSION_DEFAULT)
  {
    now = timer_get_tick();
    if ((now - last_tester_present_tick) >= SESSION_TIMEOUT_MS)
    {
      session_reset_to_default();
    }
  }

  uds_process_message(data, len);
}

/**
 * @brief  CAN RX callback for UDS protocol handling
 * @note   called from can_driver_poll() in main loop context.
 * @param  id:   29-bit extended identifier of received frame
 * @param  data: pointer to received data buffer
 * @param  len:  data length (0~8)
 * @retval none
 */
static void can_protocol_rx_handler(uint32_t id, uint8_t *data, uint8_t len)
{
  /* physical request or functional broadcast */
  if ((id != CAN_PROTO_UDS_REQUEST) &&
      ((id & 0x1FFFFF00U) != 0x18DB3300U))
  {
    return;
  }

  if (len == 0)
  {
    return;
  }

  can_lp_mark_uds();
  isotp_rx_process(data, len);
}

/* ========================================================================== */
/*  Exported functions                                                       */
/* ========================================================================== */

/**
 * @brief  initialize CAN protocol module
 * @param  none
 * @retval none
 */
void can_protocol_init(void)
{
  current_session          = SESSION_DEFAULT;
  security_unlocked        = 0;
  last_tester_present_tick = timer_get_tick();
  g_can_awake              = 0U;
  g_need_lifecycle_announce = 0U;
  g_uds_last_ms            = timer_get_tick();

  isotp_init(isotp_message_received);
  can_driver_register_rx_callback(can_protocol_rx_handler);
  qi_protocol_register_callback(qi_iap_frame_cb);

  /* load persistent Qi config from NVM (nvm_drv_init already called in main) */
  qi_nvm_load_config();

  /* 上电即 Normal，空闲 CAN_LP_IDLE_TIMEOUT_MS（UDS 无收发）后进 Standby。
   * trial 与非 trial 同路径置 g_lp_need_online，由首次 can_protocol_poll
   * 延时 can_lp_enter_normal（SysTick 已就绪；harvest / sit1145_wait_cts /
   * wait_tx_idle 都看 timer_get_tick）完成 sit1145_normal_mode_set +
   * can_driver_online。 */
  /* trial / 非 trial 统一上电 Normal，首次 poll 延时 enter_normal */
  g_lp_need_online = 1U;
}

/**
 * @brief  Qi IAP ACK poll: non-blocking check for Qi chip UART ACK
 * @note   Called from can_protocol_poll() when state == WAIT_ACK.
 *         On ACK: sends deferred UDS positive response, resumes IAP_IN_PROGRESS.
 *         On NAK: sends NRC 0x72, resumes IAP_IN_PROGRESS (allow host retry).
 *         On timeout: sends NRC 0x72, resumes IAP_IN_PROGRESS (allow host retry).
 */
static void qi_iap_ack_poll(void)
{
  uint32_t now;

  if (g_qi_iap_state != QI_IAP_WAIT_ACK)
  {
    return;
  }

  /* flush any pending UART bytes from Qi chip */
  qi_protocol_poll();

  now = timer_get_tick();

  /* check if callback already received an ACK or FAILED */
  if ((g_qi_iap_state == QI_IAP_IN_PROGRESS) || (g_qi_iap_state == QI_IAP_SUCCESS))
  {
    /* ACK received (callback left WAIT_ACK)
     * Send deferred positive response */
    uint8_t resp[3];
    resp[0] = UDS_SID_WRITE_DATA_BY_ID + UDS_POSITIVE_RESPONSE_OFFSET;
    resp[1] = g_qi_iap_pending_did[0];
    resp[2] = g_qi_iap_pending_did[1];
    proto_send_response(resp, 3);
    return;
  }
  if (g_qi_iap_state == QI_IAP_FAILED)
  {
    /* NAK from Qi chip — allow host retry */
    g_qi_iap_state = QI_IAP_IN_PROGRESS;
    proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
    return;
  }

  /* timeout: no response from Qi chip */
  if ((now - g_qi_iap_wait_start_ms) >= g_qi_iap_ack_timeout_ms)
  {
    g_qi_iap_state = QI_IAP_IN_PROGRESS;
    g_qi_iap_pending_chunk = 0U;
    proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
  }
}

/**
 * @brief  Qi 版本问询超时兜底（DID 0x2013 延迟应答）
 * @note   Called from can_protocol_poll()。回复到达走帧回调；
 *         此处只处理 500ms 超时：有缓存（g_qi_fw_version_valid）用缓存
 *         整数拼完整版本串 62 20 13 + ASCII "QC_JYF_MCU2_FW_1.1.X"
 *         （变长 23~27B，产线不断流），无缓存回 NRC 0x22
 *         （如需严格问询语义可去掉缓存兜底）。
 *         版本 0 为合法值，有效性以 g_qi_fw_version_valid 为准。
 */
static void qi_ver_query_poll(void)
{
  if (g_qi_ver_q_state != 1U)
  {
    return;
  }
  if ((timer_get_tick() - g_qi_ver_q_start_ms) < QI_VER_QUERY_TIMEOUT_MS)
  {
    return;
  }
  g_qi_ver_q_state = 0U;
  if (g_qi_fw_version_valid != 0U)
  {
    /* 缓存为完整整数版本值（问询回复与 0x01 上报同源），直接拼串 */
    qi_ver_send_full_response(g_qi_fw_version);
  }
  else
  {
    proto_send_nrc(UDS_SID_READ_DATA_BY_ID, UDS_NRC_CONDITIONS_NOT_CORRECT);
  }
}

void can_protocol_poll(void)
{
  uint32_t now;
  static uint32_t sit_last;

  /* Drain Qi UART so 0x01 reports don't overflow the 64B RX buffer.
   * Skip while WAIT_ACK: qi_iap_ack_poll() owns the parser then, otherwise
   * the ACK would be consumed here and the deferred UDS response never sent. */
  if (g_qi_iap_state != QI_IAP_WAIT_ACK)
  {
    qi_protocol_poll();
  }

  if (g_lp_need_online != 0U)
  {
    g_lp_need_online = 0U;
    can_lp_enter_normal();
  }

  now = timer_get_tick();

  /* Qi IAP ACK poll: non-blocking check for Qi chip UART ACK */
  qi_iap_ack_poll();
  qi_ver_query_poll();
  ota_dl_poll();

  /* Qi IAP auto-complete: if all data sent and no ACK within timeout,
   * assume success — but only if Qi chip has not reported FAILED.
   * Poll UART once more to flush any pending FAILED ACK before
   * overwriting state. This closes the race where can_protocol_poll()
   * runs before qi_uart_poll() on the same loop iteration. */
  if ((g_qi_iap_state == QI_IAP_IN_PROGRESS) &&
      (g_qi_iap_total > 0U) &&
      (g_qi_iap_sent >= g_qi_iap_total) &&
      (g_qi_iap_last_tx_ms != 0U) &&
      ((now - g_qi_iap_last_tx_ms) >= QI_IAP_DONE_TIMEOUT_MS))
  {
    /* flush any pending UART bytes so FAILED ACK is not missed */
    qi_protocol_poll();
    if (g_qi_iap_state == QI_IAP_IN_PROGRESS)
    {
      g_qi_iap_state    = QI_IAP_SUCCESS;
      g_qi_iap_progress = 100U;
    }
  }

  if (g_can_awake == 0U)
  {
    if ((now - g_standby_since_ms) >= CAN_LP_WAKE_INHIBIT_MS)
    {
      uint8_t src = 0U;
      uint8_t allow_wake = 0U;
      uint8_t ev24;
      uint8_t ev63;
      uint8_t spi_ok;
      uint8_t can_wake;
      uint8_t rx_low;

      /* Round-3 anti-phantom wake policy (bench TC-S002, 1.1.4 firmware =
       * round-2 fix 9f4cf24, 6 rounds 3 PASS / 3 FAIL; every FAIL is WK
       * 01 41 57 4B at SB+124~141ms = inhibit expiry + first wake poll,
       * src=1, ZERO frames on the bus, and DID 0x211A wake snapshot shows
       * ev63 CW=0x01 — r4 stat=0x81 (PA11 high at pre-check, low µs later
       * inside wakeup_pending), r6 stat=0x83 (CAN flag already set at the
       * first pin-low pre-check). CW set with no frame anywhere = PHANTOM
       * CAN-wake latch: round-2's "flag set => real wake, skip the clear"
       * rule walked straight through it (r6) and wakeup_pending()'s
       * PA11-first shortcut walked through it before flags were even
       * consulted (r4). Round-3:
       *   (b) ONE-SHOT UNCONDITIONAL wipe of 0x24/0x63/0x64/0x61 on the
       *       first poll at inhibit expiry — no flag-state test; pre/post
       *       readback recorded for 0x211A (PREW_FLAG/POST_STICK). Anything
       *       latched during entry/settle/0..100ms is erased; only events
       *       AFTER this wipe can authorize a wake.
       *   (c) flags are read FIRST, then PA11 (a latch landing between the
       *       two reads falls into the pin-low branch, never wakes on the
       *       pin alone):
       *       - CAN flag set + RXD released => real wake (bench genuine
       *         wakes: src=3, RXD already released). Wake immediately,
       *         zero added latency, src computed from these same flags
       *         (ev24 CW/WUF->2 else ev63 CW->3, byte-identical to
       *         wakeup_pending's result). TC-S003 path untouched.
       *       - RXD low (flag or not, healthy SPI) => suspected phantom:
       *         clear + bounded <=5ms release wait; flag 0->1 AFTER the
       *         clear = host frame retry (CAN auto-retransmit / 3E burst)
       *         => real wake, allow (FLAG_IN_WAIT). RXD released and
       *         silent => phantom consumed, keep sleeping. RXD stuck low
       *         => block this poll, throttled retry (CAN_LP_WIPE_RETRY_MS).
       *         A pin-low level alone (WK src=1) can never wake again.
       *       - SPI 0xFF => historical fall-through via wakeup_pending (a
       *         dead SPI must never swallow a bus wake) — the ONLY remaining
       *         path where the raw pin decides, kept byte-identical to
       *         round-2 (SPI-dead + pin low => src=1 wake).
       * Direction (d) raw-RX sniff rejected: in Standby the SIT1145 RXD
       * carries wake-event levels, not frame data — sniffing frames requires
       * the transceiver in Normal, which already means we committed to the
       * wake; delaying WK by 1-5ms would also eat TC-S003's verified
       * sub-ms/100ms wake timing. */

      /* (b) one-shot unconditional event wipe at inhibit expiry */
      if (g_lp_pre_wipe_due != 0U)
      {
        g_lp_pre_wipe_due = 0U;
        ev24 = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_EVENT);
        ev63 = sit1145_read_reg(SIT1145_REG_TRX_EVENT_STATUS);
        g_lp_wipe_ev24  = ev24;   /* pre-wipe snapshot (r6-type evidence) */
        g_lp_wipe_ev63  = ev63;
        g_lp_wipe_stat |= WIPE_STAT_VALID;
        if (((ev24 != 0xFFU) && ((ev24 & (SIT1145_CW | SIT1145_WUF)) != 0U)) ||
            ((ev63 != 0xFFU) && ((ev63 & SIT1145_TRX_EVT_STA_CW) != 0U)))
        {
          g_lp_wipe_stat |= WIPE_STAT_PREW_FLAG;
        }
        if ((ev24 == 0xFFU) || (ev63 == 0xFFU))
        {
          g_lp_wipe_stat |= WIPE_STAT_SPI_FF;
        }
        sit1145_wakeup_clear();    /* 0x61/0x63/0x64/0x24, unconditional */
        if (g_lp_wipe_attempts < 0xFFU)
        {
          g_lp_wipe_attempts++;    /* counted for 0x211A [3] */
        }
        /* readback: flag must be gone; still set = wipe ineffective or
         * instant re-latch during transceiver settle — retry once, then
         * record so the next bench run can tell the two apart */
        ev24 = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_EVENT);
        ev63 = sit1145_read_reg(SIT1145_REG_TRX_EVENT_STATUS);
        if (((ev24 != 0xFFU) && ((ev24 & (SIT1145_CW | SIT1145_WUF)) != 0U)) ||
            ((ev63 != 0xFFU) && ((ev63 & SIT1145_TRX_EVT_STA_CW) != 0U)))
        {
          sit1145_wakeup_clear();
          ev24 = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_EVENT);
          ev63 = sit1145_read_reg(SIT1145_REG_TRX_EVENT_STATUS);
          if (((ev24 != 0xFFU) && ((ev24 & (SIT1145_CW | SIT1145_WUF)) != 0U)) ||
              ((ev63 != 0xFFU) && ((ev63 & SIT1145_TRX_EVT_STA_CW) != 0U)))
          {
            g_lp_wipe_stat |= WIPE_STAT_POST_STICK;
          }
        }
        /* deliberately NOT stamping g_lp_wipe_last_ms: the first pin-low
         * clear+wait after this wipe must be allowed immediately */
      }

      /* decision: flags first, then PA11 — one consistent snapshot; the
       * r4 µs race (PA11 high at pre-check, low inside wakeup_pending) now
       * lands in the pin-low branch below instead of waking on the pin */
      ev24   = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_EVENT);
      ev63   = sit1145_read_reg(SIT1145_REG_TRX_EVENT_STATUS);
      spi_ok = ((ev24 != 0xFFU) && (ev63 != 0xFFU)) ? 1U : 0U;
      can_wake = 0U;
      if (spi_ok != 0U)
      {
        if ((ev24 & (SIT1145_CW | SIT1145_WUF)) != 0U)
        {
          can_wake = 1U;
        }
        if ((ev63 & SIT1145_TRX_EVT_STA_CW) != 0U)
        {
          can_wake = 1U;
        }
      }
      rx_low = (gpio_input_data_bit_read(GPIOA, GPIO_PINS_11) == RESET) ? 1U : 0U;

      if (spi_ok == 0U)
      {
        /* SPI unhealthy (either read 0xFF): historical fall-through,
         * unchanged from round-2 — wakeup_pending decides (PA11-first), so
         * a dead SPI can never swallow a bus wake. */
        g_lp_wipe_stat |= WIPE_STAT_VALID | WIPE_STAT_SPI_FF;
        g_lp_wipe_ev24  = ev24;
        g_lp_wipe_ev63  = ev63;
        src = sit1145_wakeup_pending();
        allow_wake = (src != 0U) ? 1U : 0U;
      }
      else if ((can_wake != 0U) && (rx_low == 0U))
      {
        /* Real-wake signature: CAN flag set AND RXD already released —
         * every genuine wake on the bench (r5 baseline: ev63=0x01,
         * src=3). Wake with zero added latency; src from THIS snapshot
         * (ev24 CW/WUF -> 2 else ev63 CW -> 3) — byte-identical to what
         * sit1145_wakeup_pending() would return, no pin race possible. */
        g_lp_wipe_stat |= WIPE_STAT_VALID;
        g_lp_wipe_ev24  = ev24;
        g_lp_wipe_ev63  = ev63;
        allow_wake = 1U;
        src = ((ev24 & (SIT1145_CW | SIT1145_WUF)) != 0U) ? 2U : 3U;
      }
      else if (rx_low != 0U)
      {
        uint8_t flag_now = 0U;

        /* RXD low, healthy SPI — phantom suspect (flag set: r6-type) or
         * round-2 stale pin-low (no flag). Never wake on the level alone.
         * Throttle rationale unchanged from round-2: while RXD is stuck the
         * main loop is tight; an unthrottled <=5ms busy-wait would starve
         * the UART and CAN polls. Throttled polls do cheap flag re-reads
         * only (already done above) and block. */
        g_lp_wipe_stat |= WIPE_STAT_VALID;
        g_lp_wipe_ev24  = ev24;
        g_lp_wipe_ev63  = ev63;
        if (can_wake != 0U)
        {
          g_lp_wipe_stat |= WIPE_STAT_FLAG_PRESENT;
        }

        /* Attempt immediately when a CAN flag is set (possible real wake
         * mid-latch: RXD held low until the event is cleared, and TC-S003
         * allows only 30ms to ACK the host retransmit — the CPU-starvation
         * throttle must not delay that case); otherwise throttle to
         * CAN_LP_WIPE_RETRY_MS. A phantom flag is consumed by this clear,
         * so the bypass is self-limiting unless the noise re-latches every
         * poll (not observed: bench phantoms latch once per entry). */
        if ((g_lp_wipe_last_ms == 0U) ||
            ((now - g_lp_wipe_last_ms) >= CAN_LP_WIPE_RETRY_MS) ||
            (can_wake != 0U))
        {
          uint32_t t0;

          g_lp_wipe_last_ms = now;
          if (g_lp_wipe_attempts < 0xFFU)
          {
            g_lp_wipe_attempts++;
          }
          sit1145_wakeup_clear();

          /* Immediate post-clear read: separates a true 0->1 transition
           * (real host retry relatched during the wait) from a flag that
           * never left (wipe ineffective / instant re-latch). The latter
           * must NOT wait and must NOT wake — waiting would busy-loop every
           * poll (CPU starvation), waking would reopen the round-2 r6 hole.
           * A real wake recovers anyway: next poll sees flag+RXD-released
           * (fast path) or the host retransmit relatches within its own
           * auto-retry cadence. */
          ev24 = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_EVENT);
          ev63 = sit1145_read_reg(SIT1145_REG_TRX_EVENT_STATUS);
          g_lp_wipe_ev24 = ev24;
          g_lp_wipe_ev63 = ev63;
          if (((ev24 != 0xFFU) && ((ev24 & (SIT1145_CW | SIT1145_WUF)) != 0U)) ||
              ((ev63 != 0xFFU) && ((ev63 & SIT1145_TRX_EVT_STA_CW) != 0U)))
          {
            /* flag survived the clear: record and fall through to block
             * (flag_now stays 0; no 5ms wait below) */
            g_lp_wipe_stat |= WIPE_STAT_POST_STICK;
          }
          else
          {
            /* Bounded RXD-release wait, same <=5ms pattern as
             * can_lp_enter_normal: a stale/phantom latch drops PA11 high
             * here and the device keeps sleeping. */
            t0 = timer_get_tick();
            while ((timer_get_tick() - t0) < 5U)
            {
              if (gpio_input_data_bit_read(GPIOA, GPIO_PINS_11) != RESET)
              {
                break;
              }
            }
            /* Re-read after the clear+wait: a flag that went 0->1 here was
             * set AFTER our clear = a real host frame retry landed (CAN
             * auto-retransmit of the un-ACKed frame / 3E×3 burst), which a
             * consumed phantom cannot produce. */
            ev24 = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_EVENT);
            ev63 = sit1145_read_reg(SIT1145_REG_TRX_EVENT_STATUS);
            g_lp_wipe_ev24 = ev24;
            g_lp_wipe_ev63 = ev63;
            if (((ev24 != 0xFFU) && ((ev24 & (SIT1145_CW | SIT1145_WUF)) != 0U)) ||
                ((ev63 != 0xFFU) && ((ev63 & SIT1145_TRX_EVT_STA_CW) != 0U)))
            {
              flag_now = 1U;
              g_lp_wipe_stat |= WIPE_STAT_FLAG_IN_WAIT;
            }
          }
        }

        if (flag_now != 0U)
        {
          /* new event after our clear = real wake retry; src from the new
           * flags (2/3), never the bare-pin 1 */
          allow_wake = 1U;
          src = ((ev24 != 0xFFU) &&
                 ((ev24 & (SIT1145_CW | SIT1145_WUF)) != 0U)) ? 2U : 3U;
        }
        else if (gpio_input_data_bit_read(GPIOA, GPIO_PINS_11) == RESET)
        {
          /* RXD still low with no proven-new event — block this poll,
           * keep retrying the wipe (throttled). A real wake is caught by
           * the flag+RXD-released path above once RXD releases, or by the
           * 0->1 re-read on a later attempt while the host retransmits. */
          allow_wake = 0U;
          g_lp_wipe_stat |= WIPE_STAT_BLOCKED;
        }
        /* else: RXD released and silent after the clear — phantom
         * consumed, fall through with allow_wake=0 and keep sleeping */
      }
      /* else: flags clean + RXD released — normal quiet standby, sleep */

      if (allow_wake != 0U)
      {
        if (src == 0U)
        {
          src = 1U;   /* SPI-dead fall-through edge: wakeup_pending said pin */
        }
        g_lp_last_wake_src = src;
        /* wake-decision snapshot: decisive ev24/ev63 values for DID
         * 0x211A (the WK frame's src byte already encodes the source) */
        g_lp_wipe_ev24  = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_EVENT);
        g_lp_wipe_ev63  = sit1145_read_reg(SIT1145_REG_TRX_EVENT_STATUS);
        g_lp_wipe_stat |= WIPE_STAT_VALID | WIPE_STAT_WAKE_SNAP;
        can_lp_enter_normal();
      }
    }
  }

  if ((g_can_awake != 0U) && (g_need_lifecycle_announce != 0U) &&
      ((int32_t)(now - g_announce_due_ms) >= 0))
  {
    g_need_lifecycle_announce = 0U;
    if (g_lp_woke_from_standby != 0U)
    {
      /* 01 41 57 4B cnt src secL secH — 与上电 BOOTUP 01 41 00 区分 */
      can_lp_tx_marker(LIFECYCLE_BOOTUP, 0x57U, 0x4BU, g_lp_wup_count,
                       g_lp_last_wake_src,
                       (uint8_t)(g_lp_last_standby_sec & 0xFFU),
                       (uint8_t)((g_lp_last_standby_sec >> 8) & 0xFFU));
    }
    else
    {
      lifecycle_set_state(LIFECYCLE_BOOTUP);
    }
    lifecycle_set_state(LIFECYCLE_OPERATIONAL);
  }

  if (g_can_awake == 0U)
  {
    return;
  }

  /* Flash erase (trial confirm / NVM) stalls this single-bank MCU; CAN error
   * IRQ is missed and the controller sits in bus-off while g_can_awake=1.
   * Host then sees 0x34 timeout and no 57 4B ident (we never re-enter Normal). */
  if (can_busoff_get(CAN1) != RESET)
  {
    proto_can_busoff_recover();
    (void)sit1145_normal_mode_set();
  }

  proto_flush_pending_tx();
  isotp_poll();

  if ((now - sit_last) >= 500U)
  {
    sit_last = now;
    (void)sit1145_normal_mode_set();
    if (can_busoff_get(CAN1) != RESET)
    {
      proto_can_busoff_recover();
    }
  }

#if (!defined(CAN_LP_STANDBY_ENABLE) || (CAN_LP_STANDBY_ENABLE != 0U)) && (CAN_LP_IDLE_TIMEOUT_MS > 0U)
  /* No Standby while charging: judge straight from the Hall sensor chain
   * (g_qi_charger_enable from DID 0x2101 + board_hall_open() on PA0) - the
   * exact condition board_charge_poll() uses to drive the charge switch,
   * so it never depends on Qi-chip state reporting (g_qi_charge_state via
   * UART). Refresh-based gate: while charging, g_uds_last_ms is refreshed
   * every poll; once charging stops the idle counter restarts from zero and
   * the normal 30s timeout applies unchanged. */
  if ((g_qi_charger_enable != 0U) && (board_hall_open() == 0U))
  {
    g_uds_last_ms = now;
  }
  /* 有符号比较（同 g_announce_due_ms 判定风格）：同轮 poll 内 mark_uds（响应发送 /
   * 唤醒 enter_normal）刷新 g_uds_last_ms 后，过期 now 参与无符号减法会下溢成
   * 巨大值导致误进 Standby */
  else if ((int32_t)(now - g_uds_last_ms) >= (int32_t)CAN_LP_IDLE_TIMEOUT_MS)
  {
    can_lp_enter_standby();
    return;
  }
#endif

  if (current_session != SESSION_DEFAULT)
  {
    if ((now - last_tester_present_tick) >= SESSION_TIMEOUT_MS)
    {
      session_reset_to_default();
    }
  }
}

uint8_t can_protocol_is_bus_awake(void)
{
  return g_can_awake;
}

uint8_t can_protocol_lifecycle_tx_ready(void)
{
  if (g_can_awake == 0U)
  {
    return 0U;
  }
  if ((g_need_lifecycle_announce != 0U) &&
      ((int32_t)(timer_get_tick() - g_announce_due_ms) < 0))
  {
    return 0U;
  }
  return 1U;
}

/**
 * @brief  get current diagnostic session
 * @retval SESSION_DEFAULT, SESSION_PROGRAMMING, or SESSION_EXTENDED
 */
uint8_t can_protocol_get_session(void)
{
  return current_session;
}

/**
 * @brief  check if security access Level 1 is unlocked
 * @retval 1 = unlocked, 0 = locked
 */
uint8_t can_protocol_is_security_unlocked(void)
{
  return security_unlocked;
}
