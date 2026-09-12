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
#include "isotp.h"
#include "timer_drv.h"
#include "lifecycle.h"
#include "device_info.h"
#include "board_gpio.h"
#include "qi_protocol.h"
#include "nvm_drv.h"
#include "sha256.h"
#include "uECC.h"
#include "sit1145.h"
#include <string.h>

/* ========================================================================== */
/*  Version string constants (UTF-8, max 16 bytes including null terminator)  */
/* ========================================================================== */

static const char SW_VERSION_STR[]     = "1.1.1";
static const char BOOTLOADER_VER_STR[] = "1.1.1";
static const char HW_VERSION_STR[]     = "1.1.1";

/* same public key as Bootloader boot_verify.c */
static const uint8_t g_app_ecdsa_pubkey[65] = {
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
static uint8_t  g_seed[4];
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

/* Qi IAP auto-complete timeout after last data packet sent */
#define QI_IAP_DONE_TIMEOUT_MS  3000U
static uint32_t g_qi_iap_last_tx_ms = 0U;

static uint8_t  g_qi_iap_state    = QI_IAP_IDLE;
static uint8_t  g_qi_iap_progress = 0U;
static uint16_t g_qi_iap_total    = 0U;
static uint16_t g_qi_iap_sent     = 0U;

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

/** @brief  SIT1145 Normal + CAN online. Power-on default is Standby. */
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
/** OTA trial 推迟到 __enable_irq() 之后再 enter_normal：harvest/wait_cts 依赖 SysTick */
static uint8_t  g_lp_need_online = 0;

/** ignore self-wake for a short window after entering Standby */
#define CAN_LP_WAKE_INHIBIT_MS  100U
/** send BOOTUP after UDS has a chance to ACK/reply the wake frame */
#define CAN_LP_ANNOUNCE_DELAY_MS  100U
/** after CAN online, spin-poll RX so host hardware retransmit of 10 01 can be ACKed */
#define CAN_LP_RX_HARVEST_MS      30U

/** 30 s with no UDS RX/TX → SIT1145 Standby (ISO 11898-2 WUP can wake) */
#define CAN_LP_IDLE_TIMEOUT_MS  (30UL * 1000UL)

static void can_lp_mark_uds(void)
{
  g_uds_last_ms = timer_get_tick();
}

static uint8_t can_lp_trial_needs_normal(void)
{
  ota_metadata_t meta;

  if (ota_metadata_read(&meta) != 0)
  {
    return 0U;
  }
  if ((meta.trial_state != TRIAL_STATE_PENDING) &&
      (meta.trial_state != TRIAL_STATE_ACTIVE))
  {
    return 0U;
  }
  return (meta.trial_slot == ota_running_slot()) ? 1U : 0U;
}

static uint8_t g_lp_ident_sent;

static void can_lp_tx_marker(uint8_t b0, uint8_t b2, uint8_t b3,
                             uint8_t b4, uint8_t b5, uint8_t b6, uint8_t b7)
{
  uint8_t d[8];

  memset(d, 0, sizeof(d));
  d[0] = b0;
  d[1] = 0x41U;
  d[2] = b2;
  d[3] = b3;
  d[4] = b4;
  d[5] = b5;
  d[6] = b6;
  d[7] = b7;
  (void)can_driver_send(CAN_ID_LIFECYCLE_BROADCAST, d, 8);
  (void)can_driver_wait_tx_idle(20U);
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

static void can_lp_hold_standby(void)
{
  /* 先关 MCU CAN、再切收发器 Standby，最后才改 GPIO。
   * 若还在 Normal 就把 TXD 改成 GPIO，会在总线上打出显性。 */
  can_driver_offline();
  sit1145_wake_enable();
  (void)sit1145_standby_mode_set();
  sit1145_wakeup_clear();
  can_driver_pins_standby();
  g_can_awake = 0U;
  g_standby_since_ms = timer_get_tick();
  g_lp_ever_standby = 1U;
}

static void can_lp_enter_standby(void)
{
  if (g_can_awake != 0U)
  {
    (void)can_driver_wait_tx_idle(20U);
    session_reset_to_default();
    /* 进睡前打 06 41 53 42，总线上先看到 SB 再静音，才能确认真进了 Standby */
    can_lp_tx_marker(LIFECYCLE_SHUTDOWN, 0x53U, 0x42U, g_lp_wup_count, 0U, 0U, 0U);
  }
  can_lp_hold_standby();
}

/* ========================================================================== */
/*  Private helper functions                                                 */
/* ========================================================================== */

/**
 * @brief  send a UDS response frame
 * @param  data: pointer to response data
 * @param  len: data length
 * @retval none
 */
static void proto_send_response(uint8_t *data, uint16_t len)
{
  can_lp_mark_uds();
  (void)isotp_tx_send(CAN_PROTO_UDS_RESPONSE, data, len);
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
 * @brief  reset session to default and clear security state
 * @note   called on session timeout or switch to default session
 * @retval none
 */
static void session_reset_to_default(void)
{
  current_session   = SESSION_DEFAULT;
  security_unlocked = 0;
  g_seed_generated  = 0;
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
      char img_ver[16];
      if (ota_get_image_version(img_ver, (uint8_t)sizeof(img_ver)) == 0)
      {
        device_info_pad32(out, img_ver);
      }
      else
      {
        device_info_pad32(out, SW_VERSION_STR);
      }
      *olen = 32U;
      return 0;
    }
    case DID_BOOTLOADER_VERSION:
      device_info_pad32(out, BOOTLOADER_VER_STR);
      *olen = 32U;
      return 0;
    case DID_HW_VERSION:
      if (device_info_read(&di) == 0)
      {
        device_info_pad32(out, di.hw_version);
      }
      else
      {
        device_info_pad32(out, HW_VERSION_STR);
      }
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
      out[0] = (ota_metadata_read(&meta) == 0) ? meta.ota_state : 0xFFU;
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
      out[0] = (ota_metadata_read(&meta) == 0) ? meta.pending_slot : 0xFEU;
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
               (uint8_t)(meta.rollback_count & 0xFFU) : 0U;
      *olen = 1U;
      return 0;
    }
    case DID_CLAMP_STATE:
      /* PA0 low (magnetic field/phone present) → 0x00, PA0 high (no phone) → 0x01 */
      out[0] = (gpio_input_data_bit_read(GPIOA, GPIO_PINS_0) != RESET) ? 0x01U : 0x00U;
      *olen = 1U;
      return 0;
    case DID_SIT1145_LP_STATUS:
      /* [0] bit0=ever_standby bit1=last_wake_was_wup
       * [1] wup_count  [2-3] last_standby_sec LE */
      out[0] = (uint8_t)((g_lp_ever_standby != 0U) | ((g_lp_woke_from_standby != 0U) << 1) |
                         ((g_lp_last_wake_src & 0x0FU) << 4));
      out[1] = g_lp_wup_count;
      out[2] = (uint8_t)(g_lp_last_standby_sec & 0xFFU);
      out[3] = (uint8_t)((g_lp_last_standby_sec >> 8) & 0xFFU);
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
      out[0] = g_qi_iap_state;
      out[1] = g_qi_iap_progress;
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
    uint8_t n = 2U;
    resp[0] = UDS_SID_DIAG_SESSION_CTRL + UDS_POSITIVE_RESPONSE_OFFSET;
    resp[1] = session_type;
    /* 从 Standby WUP 醒来时附带 4 字节，UDS 窗口也能看到（否则只有 50 01） */
    if (g_lp_woke_from_standby != 0U)
    {
      resp[2] = (uint8_t)((g_lp_ever_standby != 0U) |
                          ((g_lp_woke_from_standby != 0U) << 1) |
                          ((g_lp_last_wake_src & 0x0FU) << 4));
      resp[3] = g_lp_wup_count;
      resp[4] = (uint8_t)(g_lp_last_standby_sec & 0xFFU);
      resp[5] = (uint8_t)((g_lp_last_standby_sec >> 8) & 0xFFU);
      n = 6U;
    }
    proto_send_response(resp, n);
    if (session_type == SESSION_DEFAULT)
    {
      (void)can_driver_wait_tx_idle(20U);
      can_lp_send_ident();
    }
  }
}

/**
 * @brief  ECUReset (0x11)
 * @param  data: UDS payload
 * @param  len:  payload length
 * @retval none
 */
static void handle_ecu_reset(uint8_t *data, uint16_t len)
{
  uint8_t resp[8];
  uint8_t sub_func;
  uint8_t suppress;
  uint8_t enter_ota;

  if (len < 2U)
  {
    proto_send_nrc(UDS_SID_ECU_RESET, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
    return;
  }

  sub_func = data[1] & UDS_SUBFUNC_MASK;
  suppress = data[1] & UDS_SUBFUNC_SUPPRESS_POS_RESP;

  if (sub_func != 0x01U)
  {
    proto_send_nrc(UDS_SID_ECU_RESET, UDS_NRC_SUBFUNCTION_NOT_SUPPORTED);
    return;
  }

  /* programming session + hardReset: enter bootloader Safe Mode download */
  enter_ota = (current_session == SESSION_PROGRAMMING) ? 1U : 0U;

  if (enter_ota)
  {
    if (ota_trigger_prepare() != 0)
    {
      proto_send_nrc(UDS_SID_ECU_RESET, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
      return;
    }
  }

  if (!suppress)
  {
    resp[0] = UDS_SID_ECU_RESET + UDS_POSITIVE_RESPONSE_OFFSET;
    resp[1] = sub_func;
    proto_send_response(resp, 2);
  }

  lifecycle_set_state(LIFECYCLE_SHUTDOWN);
  (void)can_driver_wait_tx_idle(20U);
  NVIC_SystemReset();
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

  resp[0] = UDS_SID_READ_DATA_BY_ID + UDS_POSITIVE_RESPONSE_OFFSET;
  pos = 1U;
  for (i = 1U; i < len; i += 2U)
  {
    uint16_t did = ((uint16_t)data[i] << 8) | (uint16_t)data[i + 1U];
    uint8_t payload[32];
    uint8_t plen = 0U;

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
 * @note   requires programming session + SecurityAccess Level 1
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
        /* Qi IAP 控制命令
         * data[3] = 0x01: 启动升级（data[4..5] = 固件大小 16-bit）
         * data[3] = 0x02: 中止升级 */
        uint8_t sub = data[3];
        if (sub == 0x01U)
        {
          uint8_t iap_data[2];
          if (len < 6U)
          {
            proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
            return;
          }
          g_qi_iap_total    = ((uint16_t)data[4] << 8) | (uint16_t)data[5];
          g_qi_iap_sent     = 0U;
          g_qi_iap_state    = QI_IAP_IN_PROGRESS;
          g_qi_iap_progress = 0U;
          iap_data[0] = data[4];
          iap_data[1] = data[5];
          (void)qi_protocol_send_iap(iap_data, 2U);
          resp[0] = UDS_SID_WRITE_DATA_BY_ID + UDS_POSITIVE_RESPONSE_OFFSET;
          resp[1] = data[1];
          resp[2] = data[2];
          proto_send_response(resp, 3);
        }
        else if (sub == 0x02U)
        {
          g_qi_iap_state = QI_IAP_IDLE;
          g_qi_iap_progress = 0U;
          g_qi_iap_total = 0U;
          g_qi_iap_sent = 0U;
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
        /* Qi IAP 数据包
         * data[3..4] = 地址 16-bit
         * data[5..] = 固件数据（最多 22 字节/帧） */
        uint8_t iap_buf[2 + QI_FRAME_MAX_DATA_LEN];
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
        iap_buf[0] = data[3];  /* addr hi */
        iap_buf[1] = data[4];  /* addr lo */
        chunk_len = (uint16_t)(len - 5U);
        if (chunk_len > QI_FRAME_MAX_DATA_LEN)
        {
          chunk_len = QI_FRAME_MAX_DATA_LEN;
        }
        memcpy(&iap_buf[2], &data[5], chunk_len);
        (void)qi_protocol_send_iap(iap_buf, (uint8_t)(2U + chunk_len));
        g_qi_iap_sent += (uint16_t)(len - 5U);
        g_qi_iap_last_tx_ms = timer_get_tick();
        if (g_qi_iap_total > 0U)
        {
          g_qi_iap_progress = (uint8_t)((uint32_t)g_qi_iap_sent * 100U / g_qi_iap_total);
          if (g_qi_iap_progress > 100U)
          {
            g_qi_iap_progress = 100U;
          }
        }
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
 * @note   APP side does not support SecurityAccess (done in bootloader safe mode).
 *         Return NRC 0x11 to indicate this service is not available in APP.
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

  sub_func = data[1];
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
      resp[0] = UDS_SID_SECURITY_ACCESS + UDS_POSITIVE_RESPONSE_OFFSET;
      resp[1] = 0x01U;
      resp[2] = 0; resp[3] = 0; resp[4] = 0; resp[5] = 0;
      proto_send_response(resp, 6);
      return;
    }
    {
      uint32_t seed_val = generate_random_seed();
      g_seed[0] = (uint8_t)((seed_val >> 24) & 0xFFU);
      g_seed[1] = (uint8_t)((seed_val >> 16) & 0xFFU);
      g_seed[2] = (uint8_t)((seed_val >> 8) & 0xFFU);
      g_seed[3] = (uint8_t)(seed_val & 0xFFU);
    }
    g_seed_generated = 1;
    g_seed_sub = 0x01U;
    g_sa_sig_bytes_received = 0;
    g_sa_sig_block_seq = 0;
    resp[0] = UDS_SID_SECURITY_ACCESS + UDS_POSITIVE_RESPONSE_OFFSET;
    resp[1] = 0x01U;
    resp[2] = g_seed[0];
    resp[3] = g_seed[1];
    resp[4] = g_seed[2];
    resp[5] = g_seed[3];
    proto_send_response(resp, 6);
  }
  else if (sub_func == 0x03U)
  {
    uint8_t block_seq;
    uint8_t chunk_len;
    uint8_t i;

    if (!g_seed_generated || (g_seed_sub != 0x01U))
    {
      proto_send_nrc(UDS_SID_SECURITY_ACCESS, UDS_NRC_SUBFUNCTION_NOT_SUPPORTED);
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

    if (!g_seed_generated || (g_seed_sub != 0x01U))
    {
      proto_send_nrc(UDS_SID_SECURITY_ACCESS, UDS_NRC_REQUEST_SEQUENCE_ERROR);
      return;
    }
    /* 0x03 already filled the buffer: ignore padded 27 02 (ZCANPRO fill 0xCC). */
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
      g_seed_generated = 0;
      proto_send_nrc(UDS_SID_SECURITY_ACCESS, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
      return;
    }
    proto_send_nrc(UDS_SID_SECURITY_ACCESS, UDS_NRC_RESPONSE_PENDING);
    sha256_hash(g_seed, 4U, hash);
    if (uECC_verify(g_app_ecdsa_pubkey, hash, g_sa_sig_buf) == 1)
    {
      security_unlocked = 1;
      g_security_fail_count = 0;
      g_seed_generated = 0;
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
 * @note   APP side only reports NRC 0x11 for all routines
 *         (erase and other routines are handled by bootloader)
 * @param  data: UDS payload
 * @param  len:  payload length
 * @retval none
 */
static void handle_routine_control(uint8_t *data, uint16_t len)
{
  (void)data;
  (void)len;
  /* All routines are handled by bootloader, not APP */
  proto_send_nrc(UDS_SID_ROUTINE_CONTROL, UDS_NRC_SERVICE_NOT_SUPPORTED);
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

/** @brief  Qi IAP ACK status codes from Qi chip (data[0] of 0xCC response) */
#define QI_IAP_ACK_OK       0x00U
#define QI_IAP_ACK_COMPLETE 0x02U
#define QI_IAP_ACK_FAILED   0x03U

/**
 * @brief  Qi frame callback: handle IAP ACK and status report (0x01)
 */
static void qi_iap_frame_cb(const qi_frame_t *frame)
{
  if (frame == (const qi_frame_t *)0)
  {
    return;
  }

  /* ---- Qi IAP ACK (0xCC) ----
   * ACK frame: data[0]=sub_cmd, data[1]=status, data[2]=reserved
   *   sub_cmd: 0x01=prepare ACK, 0x02=data ACK
   *   status:  0x00=OK */
  if (frame->cmd == QI_CMD_IAP)
  {
    if (frame->data_len < 3U)
    {
      return;
    }
    /* check status byte (data[1]) */
    if (frame->data[1] == QI_IAP_ACK_FAILED)
    {
      g_qi_iap_state = QI_IAP_FAILED;
    }
    return;
  }

  /* ---- Qi status report (0x01) ---- */
  if (frame->cmd == QI_CMD_STATUS_REPORT)
  {
    uint8_t status_byte;

    if (frame->data_len < 13U)
    {
      return;  /* expect at least 13 bytes of status data */
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

    /* output power: bytes 4-5, uint16 mW */
    g_qi_output_power_mw = (uint16_t)frame->data[4]
                         | ((uint16_t)frame->data[5] << 8);

    /* input voltage raw: byte 6 */
    g_qi_voltage_raw = frame->data[6];

    /* input current raw: byte 7 */
    g_qi_current_raw = frame->data[7];

    /* coil temperature: byte 8 - HW not supported, skip */
    /* PCB temperature: byte 8 (reuse field) */
    g_qi_pcb_temp = frame->data[8];

    /* FOD status: byte 9 */
    if (frame->data[9] != 0x00U)
    {
      g_qi_fod_status = frame->data[9];
    }

    /* alignment: byte 10 - HW not supported, skip */

    /* fault code: byte 11 */
    if (frame->data[11] != 0x00U)
    {
      g_qi_fault_code = frame->data[11];
    }

    /* thermal derating: byte 12 */
    g_qi_thermal_derate = frame->data[12];

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

    case UDS_SID_ECU_RESET:
      handle_ecu_reset(data, len);
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
    case UDS_SID_TRANSFER_DATA:
    case UDS_SID_TRANSFER_EXIT:
    case UDS_SID_TRANSFER_SIGNATURE:
      /* download path is Bootloader Safe Mode only; host must 0x10 0x02 + 0x11 */
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

  /* sit1145_init() 已进 Standby。OTA trial 必须在 SysTick 中断起来后再 enter_normal
   * （harvest / sit1145_wait_cts / wait_tx_idle 都看 timer_get_tick）。其余上电保持 Standby。 */
  if (can_lp_trial_needs_normal() != 0U)
  {
    g_lp_need_online = 1U;
  }
  else
  {
    g_lp_need_online = 0U;
    can_lp_hold_standby();
  }
}

void can_protocol_poll(void)
{
  uint32_t now;
  static uint32_t sit_last;

  if (g_lp_need_online != 0U)
  {
    g_lp_need_online = 0U;
    can_lp_enter_normal();
  }

  now = timer_get_tick();

  /* Qi IAP auto-complete: if all data sent and no ACK within timeout, assume success */
  if ((g_qi_iap_state == QI_IAP_IN_PROGRESS) &&
      (g_qi_iap_total > 0U) &&
      (g_qi_iap_sent >= g_qi_iap_total) &&
      (g_qi_iap_last_tx_ms != 0U) &&
      ((now - g_qi_iap_last_tx_ms) >= QI_IAP_DONE_TIMEOUT_MS))
  {
    g_qi_iap_state    = QI_IAP_SUCCESS;
    g_qi_iap_progress = 100U;
  }

  if (g_can_awake == 0U)
  {
    if ((now - g_standby_since_ms) >= CAN_LP_WAKE_INHIBIT_MS)
    {
      uint8_t src = sit1145_wakeup_pending();
      if (src != 0U)
      {
        g_lp_last_wake_src = src;
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

  isotp_poll();

  if ((now - sit_last) >= 500U)
  {
    sit_last = now;
    (void)sit1145_normal_mode_set();
  }

#if (CAN_LP_IDLE_TIMEOUT_MS > 0U)
  if ((now - g_uds_last_ms) >= CAN_LP_IDLE_TIMEOUT_MS)
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
