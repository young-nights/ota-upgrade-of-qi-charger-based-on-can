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
#include "sha256.h"
#include "uECC.h"
#include "sit1145.h"
#include <string.h>

/* ========================================================================== */
/*  Version string constants (UTF-8, max 16 bytes including null terminator)  */
/* ========================================================================== */

static const char SW_VERSION_STR[]     = "1.0.0";
static const char BOOTLOADER_VER_STR[] = "1.0.0";
static const char HW_VERSION_STR[]     = "1.0.0";

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

/** @brief  SIT1145 Normal + CAN online. Power-on default is Standby. */
static uint8_t  g_can_awake = 0;
static uint8_t  g_need_lifecycle_announce = 0;
static uint32_t g_uds_last_ms = 0;

/** 6 minutes with no UDS RX/TX → SIT1145 Standby */
#define CAN_LP_IDLE_TIMEOUT_MS  (0UL)  /* Standby 已禁用，SIT1145 保持 Normal */

static void can_lp_mark_uds(void)
{
  g_uds_last_ms = timer_get_tick();
}

static void can_lp_enter_normal(void)
{
  uint8_t retry;

  if (g_can_awake != 0U)
  {
    return;
  }
  for (retry = 0U; retry < 3U; retry++)
  {
    if (sit1145_normal_mode_set() != 0U)
    {
      break;
    }
    /* SIT1145 may need time to settle after bootloader handoff */
    {
      uint32_t t0 = timer_get_tick();
      while ((timer_get_tick() - t0) < 5U) { __NOP(); }
    }
  }
  if (retry >= 3U)
  {
    return;
  }
  sit1145_wakeup_clear();
  can_driver_online();
  g_can_awake = 1U;
  can_lp_mark_uds();
  g_need_lifecycle_announce = 1U;
}

static void can_lp_enter_standby(void)
{
  if (g_can_awake == 0U)
  {
    return;
  }
  (void)can_driver_wait_tx_idle(20U);
  can_driver_offline();

  {
    gpio_init_type gpio_init_struct;

    /* PA12 (CAN_TX) 从 AF4 切到 GPIO 输出低，防止上拉电阻将 SIT1145 TXD 拉高，
     * 确保总线处于 recessive 状态，SIT1145 可正常检测 ISO 11898-2 WUP 唤醒模式。
     * can_lp_enter_normal() 会恢复 PA12 为 CAN AF4。 */
    gpio_default_para_init(&gpio_init_struct);
    gpio_init_struct.gpio_pins           = GPIO_PINS_12;
    gpio_init_struct.gpio_mode           = GPIO_MODE_OUTPUT;
    gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
    gpio_init_struct.gpio_pull           = GPIO_PULL_NONE;
    gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_MODERATE;
    gpio_init(GPIOA, &gpio_init_struct);
    gpio_bits_reset(GPIOA, GPIO_PINS_12);

    /* PA11 (CAN_RX) 从 AF4 切到 GPIO 输入上拉，
     * 确保 sit1145_wakeup_pending() 能通过 gpio_input_data_bit_read()
     * 正确读取 SIT1145 驱动的 RXD 电平（唤醒时拉低）。
     * can_lp_enter_normal() 会恢复 PA11 为 CAN AF4。 */
    gpio_default_para_init(&gpio_init_struct);
    gpio_init_struct.gpio_pins           = GPIO_PINS_11;
    gpio_init_struct.gpio_mode           = GPIO_MODE_INPUT;
    gpio_init_struct.gpio_pull           = GPIO_PULL_UP;
    gpio_init(GPIOA, &gpio_init_struct);
  }

  sit1145_wake_enable();
  sit1145_wakeup_clear();
  (void)sit1145_standby_mode_set();
  g_can_awake = 0U;
  session_reset_to_default();
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
      out[0] = board_hall_open();
      *olen = 1U;
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
    case 0x21FFU:
      out[0] = sit1145_get_mode();  /* 0x04=Standby, 0x07=Normal, 0x01=Sleep */
      *olen = 1U;
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
    resp[0] = UDS_SID_DIAG_SESSION_CTRL + UDS_POSITIVE_RESPONSE_OFFSET;
    resp[1] = session_type;
    proto_send_response(resp, 2);
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

  /* minimum: SID + DID_H + DID_L + 1 byte data */
  if (len < 4U)
  {
    proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
    return;
  }

  /* check programming session */
  if (current_session != SESSION_PROGRAMMING)
  {
    proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_CONDITIONS_NOT_CORRECT);
    return;
  }

  /* check security access */
  if (!security_unlocked)
  {
    proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_SECURITY_ACCESS_DENIED);
    return;
  }

  {
    uint16_t did = ((uint16_t)data[1] << 8) | (uint16_t)data[2];

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
        resp[0] = UDS_SID_WRITE_DATA_BY_ID + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        proto_send_response(resp, 3);
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
          /* 启动 IAP：通知 Qi 芯片进入升级模式 */
          if (len < 6U)
          {
            proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
            return;
          }
          resp[0] = UDS_SID_WRITE_DATA_BY_ID + UDS_POSITIVE_RESPONSE_OFFSET;
          resp[1] = data[1];
          resp[2] = data[2];
          proto_send_response(resp, 3);
        }
        else if (sub == 0x02U)
        {
          /* 中止 IAP */
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
        if (len < 6U)
        {
          proto_send_nrc(UDS_SID_WRITE_DATA_BY_ID, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
          return;
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
  ota_metadata_t meta;

  current_session          = SESSION_DEFAULT;
  security_unlocked        = 0;
  last_tester_present_tick = timer_get_tick();
  g_can_awake              = 0U;
  g_need_lifecycle_announce = 0U;
  g_uds_last_ms            = timer_get_tick();

  isotp_init(isotp_message_received);
  can_driver_register_rx_callback(can_protocol_rx_handler);

  /* After OTA the image is in trial: host 22 2113 must work immediately.
   * Confirmed idle boots stay in SIT1145 Standby until a wake-up frame. */
  if ((ota_metadata_read(&meta) == 0) &&
      ((meta.trial_state == 1U) || (meta.trial_state == 2U)))
  {
    can_lp_enter_normal();
  }
  else
  {
    can_driver_offline();
  }
}

void can_protocol_poll(void)
{
  uint32_t now;
  static uint32_t sit_last;

  now = timer_get_tick();

  if (g_can_awake == 0U)
  {
    if (sit1145_wakeup_pending() != 0U)
    {
      uint8_t evt = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_EVENT);
      if ((evt & SIT1145_WUF) != 0U)
      {
      }
      else if ((evt & SIT1145_CW) != 0U)
      {
      }
      else
      {
      }
      can_lp_enter_normal();
    }
  }

  if ((g_can_awake != 0U) && (g_need_lifecycle_announce != 0U))
  {
    g_need_lifecycle_announce = 0U;
    lifecycle_set_state(LIFECYCLE_BOOTUP);
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
