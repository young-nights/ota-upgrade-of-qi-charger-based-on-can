/**
  **************************************************************************
  * @file     boot_safe_mode.c
  * @brief    Safe mode implementation: UDS OTA download via CAN
  **************************************************************************
  *
  * @note    This file implements the bootloader safe-mode UDS server.
  *          ISO-TP multi-frame transport is supported for all UDS services.
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
#include "boot_safe_mode.h"
#include "boot_metadata.h"
#include "boot_verify.h"
#include "boot_trial.h"
#include "can_driver.h"
#include "isotp.h"
#include "timer_drv.h"
#include "sit1145.h"
#include "uECC.h"
#include "sha256.h"
#include <string.h>

#define SW_VERSION  "1.1.1"
#define BL_VERSION  "1.1.1"
#define HW_VERSION  "1.1.1"

#define DEVICE_INFO_MAGIC        0x44455649U  /* "DEVI" */
#define DEVICE_INFO_VERSION      2U
#define DEVICE_INFO_STRUCT_SIZE  512U
#define DEVICE_INFO_PUBKEY_LEN   65U

typedef struct
{
  uint32_t magic;
  uint32_t version;
  uint32_t crc32;
  char     sn[32];
  char     hw_version[8];
  uint32_t production_date;
  uint8_t  ecdsa_pubkey[DEVICE_INFO_PUBKEY_LEN];
  uint8_t  pubkey_valid;
  uint8_t  reserved[390];
} device_info_t;

static uint32_t device_info_crc(const device_info_t *info)
{
  const uint8_t *p = (const uint8_t *)info;
  uint32_t crc = 0xFFFFFFFFU;
  uint32_t n;
  uint32_t j;
  uint32_t bit;

  for (n = 0U; n < DEVICE_INFO_STRUCT_SIZE; n++)
  {
    if ((n >= 8U) && (n < 12U))
    {
      continue;
    }
    crc ^= (uint32_t)p[n];
    for (j = 0U; j < 8U; j++)
    {
      bit = crc & 1U;
      crc >>= 1;
      if (bit != 0U)
      {
        crc ^= 0xEDB88320U;
      }
    }
  }
  return crc ^ 0xFFFFFFFFU;
}

static int8_t device_info_read(device_info_t *out)
{
  const device_info_t *flash = (const device_info_t *)DEVICE_INFO_ADDR;

  if (out == (device_info_t *)0)
  {
    return -1;
  }
  memcpy((void *)out, (const void *)flash, sizeof(device_info_t));
  if ((out->magic != DEVICE_INFO_MAGIC) ||
      (out->version != DEVICE_INFO_VERSION) ||
      (device_info_crc(out) != out->crc32))
  {
    return -1;
  }
  return 0;
}

static int8_t device_info_write_sn(const uint8_t *sn32)
{
  device_info_t info;
  const uint32_t *src;
  uint32_t words;
  uint32_t i;
  flash_status_type st;

  if (sn32 == (const uint8_t *)0)
  {
    return -1;
  }
  if (device_info_read(&info) != 0)
  {
    memset((void *)&info, 0xFF, sizeof(info));
    info.magic           = DEVICE_INFO_MAGIC;
    info.version         = DEVICE_INFO_VERSION;
    info.production_date = 0U;
    memset((void *)info.hw_version, 0, sizeof(info.hw_version));
    info.pubkey_valid    = 0xFFU;
    memset((void *)info.reserved, 0xFF, sizeof(info.reserved));
  }
  memcpy((void *)info.sn, (const void *)sn32, 32U);
  info.crc32 = device_info_crc(&info);

  flash_unlock();
  st = flash_sector_erase(DEVICE_INFO_ADDR);
  if (st != FLASH_OPERATE_DONE)
  {
    flash_lock();
    return -1;
  }
  src   = (const uint32_t *)&info;
  words = sizeof(device_info_t) / 4U;
  for (i = 0U; i < words; i++)
  {
    st = flash_word_program(DEVICE_INFO_ADDR + (i * 4U), src[i]);
    if (st != FLASH_OPERATE_DONE)
    {
      flash_lock();
      return -1;
    }
  }
  flash_lock();
  return 0;
}

/* private define ------------------------------------------------------------*/

/** @brief  safe mode CAN IDs for OTA download */
#define SAFE_MODE_CAN_ID_REQUEST    0x18DA0D03U  /*!< UDS request ID */
#define SAFE_MODE_CAN_ID_RESPONSE   0x18DA030DU  /*!< UDS response ID */

/** @brief  UDS service IDs supported in safe mode */
#define UDS_DIAG_SESSION_CTRL       0x10U
#define UDS_SECURITY_ACCESS         0x27U
#define UDS_REQUEST_DOWNLOAD        0x34U
#define UDS_TRANSFER_DATA           0x36U
#define UDS_REQUEST_TRANSFER_EXIT   0x37U
#define UDS_ECU_RESET               0x11U
#define UDS_READ_DATA_BY_ID         0x22U
#define UDS_WRITE_DATA_BY_ID        0x2EU
#define UDS_ROUTINE_CONTROL         0x31U
#define UDS_TESTER_PRESENT          0x3EU

/** @brief  UDS negative response code */
#define UDS_NEGATIVE_RESPONSE       0x7FU
#define UDS_NRC_SERVICE_NOT_SUPPORTED   0x11U
#define UDS_NRC_SUBFUNCTION_NOT_SUPPORTED 0x12U
#define UDS_NRC_INCORRECT_MSG_LENGTH  0x13U
#define UDS_NRC_RESPONSE_TOO_LONG     0x14U
#define UDS_NRC_CONDITIONS_NOT_CORRECT 0x22U
#define UDS_NRC_REQUEST_SEQUENCE_ERROR 0x24U
#define UDS_NRC_REQUEST_OUT_OF_RANGE    0x31U
#define UDS_NRC_SECURITY_ACCESS_DENIED 0x33U
#define UDS_NRC_INVALID_KEY             0x35U
#define UDS_NRC_EXCEEDED_NUMBER_OF_ATTEMPTS 0x36U
#define UDS_NRC_REQUIRED_TIME_DELAY_NOT_EXPIRED 0x37U
#define UDS_NRC_UPLOAD_DOWNLOAD_NOT_ACCEPTED 0x70U
#define UDS_NRC_TRANSFER_DATA_ABORTED 0x71U
#define UDS_NRC_GENERAL_PROGRAMMING_FAILURE 0x72U
#define UDS_NRC_WRONG_BLOCK_SEQUENCE  0x73U
#define UDS_NRC_RESPONSE_PENDING      0x78U
#define UDS_POSITIVE_RESPONSE_OFFSET 0x40U

/** @brief  Diagnostic session values */
#define SESSION_DEFAULT     0x01U
#define SESSION_PROGRAMMING 0x02U
#define SESSION_EXTENDED    0x03U

/* private variables ---------------------------------------------------------*/

/** @brief  safe mode flag */
static uint8_t g_safe_mode = 0;

/** @brief  current diagnostic session (default: 0x01) */
static uint8_t g_current_session = SESSION_DEFAULT;

/** @brief  download state variables */
static uint32_t g_dl_write_addr    = 0;
static uint32_t g_dl_bytes_written = 0;
static uint8_t  g_dl_block_seq     = 0;
static uint8_t  g_dl_active        = 0;
static uint8_t  g_dl_slot          = SLOT_A;
static uint32_t g_dl_slot_base     = APP_A_BASE_ADDR;
static uint32_t g_dl_slot_size     = APP_A_SIZE;
static uint8_t  g_dl_pad[4];
static uint8_t  g_dl_pad_len       = 0;
static uint8_t  g_dl_erased        = 0;
static uint32_t g_dl_expected_size = 0;
static uint8_t  g_xfer_exit_pending = 0;
static uint8_t  g_xfer_exit_busy    = 0;
static uint8_t  g_sa_verify_pending = 0;
static uint8_t  g_sa_verify_busy    = 0;
static uint8_t  g_pump_reentry      = 0;

static void safe_mode_finish_transfer_exit(void);
static void safe_mode_finish_security_verify(void);

/** @brief  maxNumberOfBlockLength advertised in 0x34 (SID+BSC+data) */
#define UDS_MAX_BLOCK_LEN     256U
#define UDS_S3_TIMEOUT_MS     5000U

/** @brief  periodic CAN frame so a bitrate probe sees 250 kbps in Safe Mode.
 *          Same ID as APP lifecycle; byte0=0x00 is not an APP state (0x01..0x06).
 *          Disabled: host UDS RX is being tested with SIT1145 SPI Normal keepalive. */
#define SAFE_MODE_PROBE_ENABLE     0U
#define SAFE_MODE_PROBE_PERIOD_MS  500U
#define SAFE_MODE_PROBE_STATE      0x00U

static uint8_t safe_mode_can_busoff_recover(void)
{
  uint32_t start;
  uint8_t n;

  for (n = 0U; n < 3U; n++)
  {
    if (can_busoff_get(CAN1) == RESET)
    {
      return 1U;
    }
    can_busoff_reset(CAN1);
    start = timer_get_tick();
    while ((timer_get_tick() - start) < 10U)
    {
      if (can_busoff_get(CAN1) == RESET)
      {
        return 1U;
      }
    }
  }
  /* last resort: full re-init (restores bit timing + filters) */
  can_driver_init();
  return (can_busoff_get(CAN1) == RESET) ? 1U : 0U;
}

static void safe_mode_long_op_pump(void);
static void safe_mode_begin_long_op(uint8_t service_id);
static void safe_mode_end_long_op(void);

/** @brief  rewrite SIT1145 mode control to Normal so the transceiver does not
 *          drop to CTS/Standby while Safe Mode is idle with no CAN TX. */
#define SAFE_MODE_SIT1145_KEEPALIVE_ENABLE     1U
#define SAFE_MODE_SIT1145_KEEPALIVE_PERIOD_MS  500U

static uint32_t g_s3_last_ms = 0;
#if (SAFE_MODE_PROBE_ENABLE != 0U)
static uint32_t g_probe_last_ms = 0;
#endif
#if (SAFE_MODE_SIT1145_KEEPALIVE_ENABLE != 0U)
static uint32_t g_sit1145_keepalive_last_ms = 0;
#endif



/** @brief  security access state */
static uint8_t  g_security_unlocked = 0;
static uint8_t  g_seed_generated = 0;
static uint8_t  g_seed[4];
static uint8_t  g_seed_sub = 0;
static uint8_t  g_security_fail_count = 0;
static uint32_t g_security_lockout_until_ms = 0;

/** @brief  firmware type selected by 0x2E 0x2010 (0=app, 1=bootloader) */
static uint8_t  g_firmware_type = 0;

/** @brief  SecurityAccess ECDSA signature buffer */
static uint8_t  g_sa_sig_buf[64];
static volatile uint8_t  g_sa_sig_bytes_received = 0;
static volatile uint8_t  g_sa_sig_block_seq = 0;

/** @brief  security access lockout: 30 seconds */
#define SECURITY_LOCKOUT_MS  30000U

/** @brief  maximum consecutive security access failures before lockout */
#define SECURITY_MAX_FAILURES 3U

/* private functions ---------------------------------------------------------*/

/**
 * @brief  generate a pseudo-random 32-bit seed using SysTick and LFSR
 * @retval 32-bit pseudo-random value
 */
static uint32_t generate_random_seed(void)
{
  static uint32_t lfsr = 0x12345678U;
  uint32_t tick = timer_get_tick();
  uint32_t bit;

  /* XOR tick into LFSR state for entropy */
  lfsr ^= tick;

  /* 32-bit maximal-length LFSR (taps at bits 31, 21, 1, 0) */
  bit = ((lfsr >> 0) ^ (lfsr >> 1) ^ (lfsr >> 21) ^ (lfsr >> 31)) & 1U;
  lfsr = (lfsr >> 1) | (bit << 31);

  /* additional mixing with current tick */
  lfsr ^= (tick << 7) ^ (tick >> 13);

  return lfsr;
}

/**
 * @brief  verify ECDSA P-256 signature over seed
 * @note   Verifies the 64-byte IEEE P1363 signature that the host computed
 *         over SHA256(seed) using the pre-provisioned public key.
 * @retval 1 if signature is valid, 0 if invalid
 */
static uint8_t verify_security_ecdsa_signature(void)
{
  const uint8_t *public_key;
  uint8_t seed_hash[32];
  int result;

  /* get the pre-provisioned public key */
  public_key = boot_verify_get_public_key();
  if (public_key == (const uint8_t *)0)
  {
    return 0U;
  }

  /* compute SHA-256 of the 4-byte seed */
  sha256_hash(g_seed, 4U, seed_hash);

  /* verify ECDSA P-256 signature */
  result = uECC_verify(public_key, seed_hash, g_sa_sig_buf);

  return (result == 1) ? 1U : 0U;
}

static uint8_t select_inactive_slot(void)
{
  if ((g_meta.slot_a_valid != 0U) && (g_meta.active_slot == SLOT_A))
  {
    return SLOT_B;
  }
  if ((g_meta.slot_b_valid != 0U) && (g_meta.active_slot == SLOT_B))
  {
    return SLOT_A;
  }
  if (g_meta.slot_a_valid == 0U)
  {
    return SLOT_A;
  }
  return SLOT_B;
}

static void dl_bind_slot(uint8_t slot)
{
  g_dl_slot      = slot;
  g_dl_slot_base = boot_metadata_slot_addr(slot);
  g_dl_slot_size = boot_metadata_slot_size(slot);
  if (g_dl_slot_base == 0U)
  {
    g_dl_slot      = SLOT_A;
    g_dl_slot_base = APP_A_BASE_ADDR;
    g_dl_slot_size = APP_A_SIZE;
  }
}

static void dl_reset_state(void)
{
  /* write from slot base: host sends image_header_t (256B) + firmware */
  g_dl_write_addr    = g_dl_slot_base;
  g_dl_bytes_written = 0;
  g_dl_block_seq     = 0;
  g_dl_pad_len       = 0;
  g_dl_active        = 1;
}

static void dl_abort(void)
{
  g_dl_active        = 0;
  g_dl_erased        = 0;
  g_dl_expected_size = 0;
}

static uint32_t uds_parse_be(const uint8_t *p, uint8_t n)
{
  uint32_t v = 0;
  uint8_t i;
  for (i = 0; i < n; i++)
  {
    v = (v << 8) | (uint32_t)p[i];
  }
  return v;
}

/**
 * @brief  erase all 2KB sectors of the target APP slot
 * @retval 0 on success, non-zero on error
 */
/** @brief  AT32F426 Flash physical end (128KB: 0x08000000..0x0801FFFF) */
#define FLASH_PHYSICAL_END_ADDR  0x08020000U

static uint8_t erase_slot_flash(uint8_t slot)
{
  uint32_t sector_addr;
  uint32_t base = boot_metadata_slot_addr(slot);
  uint32_t size = boot_metadata_slot_size(slot);

  if ((base == 0U) || (base < APP_A_BASE_ADDR) || (size == 0U))
  {
    return 1U;
  }

  /* never erase Device Info (0x0801D000) */
  if ((base < (DEVICE_INFO_ADDR + DEVICE_INFO_SIZE)) &&
      ((base + size) > DEVICE_INFO_ADDR))
  {
    return 1U;
  }

  /* verify erase range does not exceed Flash physical boundary */
  if ((base + size) > FLASH_PHYSICAL_END_ADDR)
  {
    return 1U;
  }

  flash_unlock();
  for (sector_addr = base; sector_addr < (base + size); sector_addr += FLASH_SECTOR_SIZE)
  {
    if (flash_sector_erase(sector_addr) != FLASH_OPERATE_DONE)
    {
      flash_lock();
      return 1U;
    }
    safe_mode_long_op_pump();
  }
  flash_lock();
  return 0U;
}

static uint8_t program_image_bytes(const uint8_t *src, uint16_t src_len)
{
  uint16_t n = src_len;
  uint16_t idx = 0;
  flash_status_type flash_status;
  uint32_t max_len = g_dl_slot_size;

  while (n > 0U)
  {
    while ((g_dl_pad_len < 4U) && (n > 0U))
    {
      g_dl_pad[g_dl_pad_len++] = src[idx++];
      n--;
    }
    if (g_dl_pad_len < 4U)
    {
      break;
    }

    if ((g_dl_bytes_written + 4U) > max_len)
    {
      return 1U;
    }

    {
      uint32_t word_data;
      word_data  =  (uint32_t)g_dl_pad[0];
      word_data |= ((uint32_t)g_dl_pad[1] << 8);
      word_data |= ((uint32_t)g_dl_pad[2] << 16);
      word_data |= ((uint32_t)g_dl_pad[3] << 24);
      flash_status = flash_word_program(g_dl_write_addr, word_data);
      if (flash_status != FLASH_OPERATE_DONE)
      {
        return 1U;
      }
    }

    g_dl_write_addr    += 4U;
    g_dl_bytes_written += 4U;
    g_dl_pad_len        = 0;
  }

  return 0U;
}

static uint8_t program_image_flush(void)
{
  uint32_t word_data;
  uint8_t k;
  flash_status_type flash_status;

  if (g_dl_pad_len == 0U)
  {
    return 0U;
  }

  word_data = 0xFFFFFFFFU;
  for (k = 0; k < g_dl_pad_len; k++)
  {
    word_data &= ~((uint32_t)0xFFU << (k * 8U));
    word_data |= ((uint32_t)g_dl_pad[k] << (k * 8U));
  }

  flash_status = flash_word_program(g_dl_write_addr, word_data);
  if (flash_status != FLASH_OPERATE_DONE)
  {
    return 1U;
  }

  g_dl_write_addr    += (uint32_t)g_dl_pad_len;
  g_dl_bytes_written += (uint32_t)g_dl_pad_len;
  g_dl_pad_len        = 0;
  return 0U;
}

/**
 * @brief  send a CAN response frame via ISO-TP
 * @param  data: pointer to UDS response payload
 * @param  len:  payload length in bytes
 * @retval none
 */
static void safe_mode_send_response(uint8_t *data, uint16_t len)
{
  isotp_tx_send(SAFE_MODE_CAN_ID_RESPONSE, data, len);
}

/**
 * @brief  send UDS negative response
 * @param  service_id: the rejected service ID
 * @param  nrc: negative response code
 * @retval none
 */
static void safe_mode_send_nrc(uint8_t service_id, uint8_t nrc)
{
  uint8_t resp[3];
  resp[0] = UDS_NEGATIVE_RESPONSE;
  resp[1] = service_id;
  resp[2] = nrc;
  safe_mode_send_response(resp, 3);
}

/**
 * @brief  send NRC 0x78 ResponsePending before a long operation
 * @note   ECDSA verification (200-800ms) and Flash erase (1-2s) exceed P2 (50ms).
 *         ISO 14229 requires NRC 0x78 to keep the host waiting.
 * @param  service_id: the service that will take time
 * @retval none
 */
static void safe_mode_send_pending(uint8_t service_id)
{
  /* Non-blocking single-frame 0x78: use can_driver_send directly.
   * isotp_tx_send retries for 1s on bus-off, which stalls the main loop. */
  uint8_t sf[8];
  sf[0] = 0x03U;  /* ISO-TP SF, DLC=3 */
  sf[1] = UDS_NEGATIVE_RESPONSE;
  sf[2] = service_id;
  sf[3] = UDS_NRC_RESPONSE_PENDING;  /* 0x78 */
  sf[4] = 0xCCU; sf[5] = 0xCCU; sf[6] = 0xCCU; sf[7] = 0xCCU;
  (void)can_driver_send(SAFE_MODE_CAN_ID_RESPONSE, sf, 8);
}

static uint8_t  g_long_op_sid;
static uint32_t g_long_op_last_78_ms;

/**
 * @brief  During 0x37 verify the main loop is blocked. Keep SIT1145 in
 *         Normal, re-send NRC 0x78 every 2 s (ISO 14229 P2*), and poll
 *         CAN so a retried 0x37 / TesterPresent still get a response.
 */
static void safe_mode_long_op_pump(void)
{
  uint32_t now;

  if (g_pump_reentry != 0U)
  {
    return;
  }
  g_pump_reentry = 1U;

  now = timer_get_tick();
  g_s3_last_ms = now;

#if (SAFE_MODE_SIT1145_KEEPALIVE_ENABLE != 0U)
  if ((now - g_sit1145_keepalive_last_ms) >= SAFE_MODE_SIT1145_KEEPALIVE_PERIOD_MS)
  {
    g_sit1145_keepalive_last_ms = now;
    (void)sit1145_normal_mode_set();
  }
#else
  (void)sit1145_normal_mode_set();
#endif

  if ((now - g_long_op_last_78_ms) >= 2000U)
  {
    g_long_op_last_78_ms = now;
    (void)safe_mode_can_busoff_recover();
    (void)sit1145_normal_mode_set();
    safe_mode_send_pending(g_long_op_sid);
    (void)can_driver_wait_tx_idle(10U);
  }

  /* Nested poll while 0x37 / 0x27 02 run from the main loop (not ISO-TP). */
  if ((g_xfer_exit_busy != 0U) || (g_sa_verify_busy != 0U))
  {
    can_driver_poll();
    isotp_poll();
  }

  g_pump_reentry = 0U;
}

static void safe_mode_begin_long_op(uint8_t service_id)
{
  g_long_op_sid = service_id;
  (void)safe_mode_can_busoff_recover();
  (void)sit1145_normal_mode_set();
  safe_mode_send_pending(service_id);
  /* Must wait for TX to actually leave the CAN controller before Flash stalls CPU */
  (void)can_driver_wait_tx_idle(50U);
  g_long_op_last_78_ms = timer_get_tick();
}

static void safe_mode_end_long_op(void)
{
  (void)sit1145_normal_mode_set();
  (void)can_driver_wait_tx_idle(50U);
}

/**
 * @brief  handle UDS request (called from ISO-TP callback)
 * @param  data: pointer to complete UDS payload (PCI already stripped)
 * @param  len:  payload length in bytes
 * @retval none
 */
static void safe_mode_xfer_exit_abort(uint8_t nrc);
static void uds_process_message(uint8_t *data, uint16_t len)
{
  uint8_t service_id;
  uint8_t resp[40];

  if ((data == (uint8_t *)0) || (len == 0U))
  {
    return;
  }

  g_s3_last_ms = timer_get_tick();
  service_id = data[0];

  /* Long ECDSA/verify owns the loop. TesterPresent is handled; others get 0x78. */
  if ((g_xfer_exit_busy != 0U) || (g_sa_verify_busy != 0U))
  {
    if (service_id != UDS_TESTER_PRESENT)
    {
      safe_mode_send_pending(g_long_op_sid);
      return;
    }
  }

  switch (service_id)
  {
    case UDS_DIAG_SESSION_CTRL:
    {
      uint8_t sub_func;
      uint8_t suppress;

      if (len < 2U)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_INCORRECT_MSG_LENGTH);
        break;
      }

      sub_func = data[1] & 0x7FU;  /* strip suppressPositiveResponse bit */
      suppress = data[1] & 0x80U;

      /* validate session value */
      if (sub_func != SESSION_DEFAULT &&
          sub_func != SESSION_PROGRAMMING &&
          sub_func != SESSION_EXTENDED)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_SUBFUNCTION_NOT_SUPPORTED);
        break;
      }

      /* switching to default session clears security unlock and aborts transfer.
       * fail-count is kept so lockout cannot be reset by 0x10 0x01. */
      if (sub_func == SESSION_DEFAULT)
      {
        g_security_unlocked = 0;
        dl_abort();
        if ((g_meta.slot_a_valid != 0U) || (g_meta.slot_b_valid != 0U))
        {
          g_meta.ota_state = OTA_STATE_IDLE;
          (void)boot_metadata_save(&g_meta);
        }
      }

      /* switching between non-default sessions clears security state */
      if (g_current_session != SESSION_DEFAULT && sub_func != SESSION_DEFAULT &&
          g_current_session != sub_func)
      {
        g_security_unlocked = 0;
      }

      g_current_session = sub_func;

      /* send positive response only if suppress bit is not set */
      if (!suppress)
      {
        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = sub_func;
        safe_mode_send_response(resp, 2);
      }
      break;
    }

    case UDS_ECU_RESET:
    {
      uint8_t sub_func;
      uint8_t suppress;

      if (len < 2U)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_INCORRECT_MSG_LENGTH);
        break;
      }

      sub_func = data[1] & 0x7FU;
      suppress = data[1] & 0x80U;

      if (sub_func != 0x01U)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_SUBFUNCTION_NOT_SUPPORTED);
        break;
      }

      if (!suppress)
      {
        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = sub_func;
        safe_mode_send_response(resp, 2);
      }

      (void)can_driver_wait_tx_idle(20U);

      NVIC_SystemReset();
      /* not reached */
      break;
    }

    case UDS_TESTER_PRESENT:
    {
      uint8_t sub_func;
      uint8_t suppress;

      if (len < 2U)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_INCORRECT_MSG_LENGTH);
        break;
      }

      sub_func = data[1] & 0x7FU;
      suppress = data[1] & 0x80U;

      if (sub_func != 0x00U)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_SUBFUNCTION_NOT_SUPPORTED);
        break;
      }

      if (!suppress)
      {
        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = sub_func;
        safe_mode_send_response(resp, 2);
      }
      break;
    }

    case UDS_SECURITY_ACCESS:
    {
      uint8_t sub_func;

      if (len < 2U)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_INCORRECT_MSG_LENGTH);
        break;
      }

      sub_func = data[1];

      if (sub_func == 0x01U)
      {
        /* Sub-function 0x01: Request Seed */
        uint32_t now_ms;

        now_ms = timer_get_tick();
        if (g_security_fail_count >= SECURITY_MAX_FAILURES)
        {
          if ((int32_t)(now_ms - g_security_lockout_until_ms) < 0)
          {
            safe_mode_send_nrc(service_id, UDS_NRC_REQUIRED_TIME_DELAY_NOT_EXPIRED);
            break;
          }
          g_security_fail_count = 0;
        }

        /* already unlocked: ISO 14229 returns seed 0 */
        if (g_security_unlocked)
        {
          resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
          resp[1] = sub_func;
          resp[2] = 0U;
          resp[3] = 0U;
          resp[4] = 0U;
          resp[5] = 0U;
          safe_mode_send_response(resp, 6);
          break;
        }

        {
          uint32_t seed_val = generate_random_seed();
          g_seed[0] = (uint8_t)((seed_val >> 24) & 0xFFU);
          g_seed[1] = (uint8_t)((seed_val >> 16) & 0xFFU);
          g_seed[2] = (uint8_t)((seed_val >> 8) & 0xFFU);
          g_seed[3] = (uint8_t)(seed_val & 0xFFU);
        }
        g_seed_generated = 1;
        g_seed_sub = sub_func;
        g_sa_sig_bytes_received = 0;
        g_sa_sig_block_seq = 0;

        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = sub_func;
        resp[2] = g_seed[0];
        resp[3] = g_seed[1];
        resp[4] = g_seed[2];
        resp[5] = g_seed[3];
        safe_mode_send_response(resp, 6);
      }
      else if (sub_func == 0x03U)
      {
        /* Sub-function 0x03: Transfer Signature Chunk (for ECDSA P-256)
         * Host sends 64-byte signature in chunks: 6 bytes per frame.
         * Frame format: [0x27, 0x03, blockSeq, sig_byte0..sig_byte5]
         * First frame resets accumulation.  Total ~11 frames for 64 bytes. */
        uint8_t block_seq;
        uint8_t chunk_len;
        uint8_t i;

        if (!g_seed_generated || g_seed_sub != 0x01U)
        {
          safe_mode_send_nrc(service_id, UDS_NRC_SUBFUNCTION_NOT_SUPPORTED);
          break;
        }

        if (len < 3U)
        {
          safe_mode_send_nrc(service_id, UDS_NRC_INCORRECT_MSG_LENGTH);
          break;
        }

        block_seq = data[2];

        /* first frame: blockSeq == 1, reset buffer */
        if (block_seq == 0x01U)
        {
          g_sa_sig_block_seq = 0;
          g_sa_sig_bytes_received = 0;
          memset(g_sa_sig_buf, 0, 64);
        }

        g_sa_sig_block_seq++;
        /* skip 0x00 on wraparound: host jumps from 0xFF to 0x01 */
        if (g_sa_sig_block_seq == 0x00U)
        {
          g_sa_sig_block_seq = 0x01U;
        }
        if (block_seq != g_sa_sig_block_seq)
        {
          /* sequence error */
          g_sa_sig_bytes_received = 0;
          safe_mode_send_nrc(service_id, UDS_NRC_TRANSFER_DATA_ABORTED);
          break;
        }

        chunk_len = (uint8_t)(len - 3U); /* subtract SID, sub_func, blockSeq */
        /* truncate if host pads last frame with zeros beyond 64-byte signature */
        if ((g_sa_sig_bytes_received + chunk_len) > 64U)
        {
          chunk_len = 64U - g_sa_sig_bytes_received;
        }

        /* accumulate signature data */
        for (i = 0; i < chunk_len; i++)
        {
          g_sa_sig_buf[g_sa_sig_bytes_received + i] = data[3U + i];
        }
        g_sa_sig_bytes_received += chunk_len;

        /* positive response */
        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = sub_func;
        resp[2] = block_seq;
        safe_mode_send_response(resp, 3);
      }
      else if (sub_func == 0x02U)
      {
        /* SendKey: do not ECDSA inside ISO-TP. Seed verify is the same
         * uECC_verify as 0x37 and can block several seconds; host then
         * only sees [Tx] 27 02 with no [Rx]. */
        if (g_security_unlocked != 0U)
        {
          resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
          resp[1] = sub_func;
          safe_mode_send_response(resp, 2);
          break;
        }

        if (!g_seed_generated || g_seed_sub != 0x01U)
        {
          safe_mode_send_nrc(service_id, UDS_NRC_REQUEST_SEQUENCE_ERROR);
          break;
        }

        if ((g_sa_sig_bytes_received != 64U) && (len >= 66U))
        {
          uint16_t k;
          for (k = 0; k < 64U; k++)
          {
            g_sa_sig_buf[k] = data[2U + k];
          }
          g_sa_sig_bytes_received = 64U;
        }

        if (g_sa_sig_bytes_received != 64U)
        {
          g_seed_generated = 0;
          g_sa_sig_bytes_received = 0;
          safe_mode_send_nrc(service_id, UDS_NRC_INCORRECT_MSG_LENGTH);
          break;
        }

        g_long_op_sid = service_id;
        g_long_op_last_78_ms = timer_get_tick();
        (void)sit1145_normal_mode_set();
        safe_mode_send_pending(service_id);
        (void)can_driver_wait_tx_idle(50U);
        g_sa_verify_busy = 1U;
        g_sa_verify_pending = 1U;
      }
      else
      {
        /* unsupported sub-function */
        safe_mode_send_nrc(service_id, UDS_NRC_SUBFUNCTION_NOT_SUPPORTED);
      }
      break;
    }

    case UDS_READ_DATA_BY_ID:
    {
      uint16_t did;

      if (len < 3U)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_INCORRECT_MSG_LENGTH);
        break;
      }

      did = ((uint16_t)data[1] << 8) | (uint16_t)data[2];

      if ((did == 0xF180U) || (did == 0xF193U) || (did == 0xF195U))
      {
        const char *src = (did == 0xF193U) ? HW_VERSION : BL_VERSION;
        uint8_t i;

        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        for (i = 0U; i < 32U; i++)
        {
          resp[3U + i] = 0x20U;
        }
        for (i = 0U; (i < 32U) && (src[i] != '\0'); i++)
        {
          resp[3U + i] = (uint8_t)src[i];
        }
        safe_mode_send_response(resp, 35U);
      }
      else if (did == 0xF18CU)
      {
        device_info_t di;
        uint8_t i;

        if (device_info_read(&di) != 0)
        {
          safe_mode_send_nrc(service_id, UDS_NRC_REQUEST_OUT_OF_RANGE);
          break;
        }
        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        for (i = 0U; i < 32U; i++)
        {
          resp[3U + i] = 0x20U;
        }
        for (i = 0U; (i < 32U) && (di.sn[i] != '\0'); i++)
        {
          resp[3U + i] = (uint8_t)di.sn[i];
        }
        safe_mode_send_response(resp, 35U);
      }
      else if (did == 0x2112U)
      {
        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        resp[3] = g_meta.ota_state;
        safe_mode_send_response(resp, 4);
      }
      else if (did == 0x2113U)
      {
        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        resp[3] = g_meta.active_slot;
        safe_mode_send_response(resp, 4);
      }
      else if (did == 0x2114U)
      {
        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        resp[3] = (g_dl_erased != 0U) ? g_dl_slot : g_meta.pending_slot;
        safe_mode_send_response(resp, 4);
      }
      else if (did == 0x2115U)
      {
        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        resp[3] = g_meta.last_boot_reason;
        safe_mode_send_response(resp, 4);
      }
      else if (did == 0x2116U)
      {
        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        resp[3] = (uint8_t)(g_meta.rollback_count & 0xFFU);
        safe_mode_send_response(resp, 4);
      }
      else
      {
        safe_mode_send_nrc(service_id, UDS_NRC_REQUEST_OUT_OF_RANGE);
      }
      break;
    }

    case UDS_WRITE_DATA_BY_ID:
    {
      uint16_t did;

      /* programming session check */
      if (g_current_session != SESSION_PROGRAMMING)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_CONDITIONS_NOT_CORRECT);
        break;
      }

      /* security gate */
      if (!g_security_unlocked)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_SECURITY_ACCESS_DENIED);
        break;
      }

      if (len < 4U)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_INCORRECT_MSG_LENGTH);
        break;
      }

      did = ((uint16_t)data[1] << 8) | (uint16_t)data[2];

      if (did == 0x2010U)
      {
        /* DID 0x2010: 0x01=APP (supported), 0x03=bootloader (not supported) */
        if (data[3] != 0x01U)
        {
          safe_mode_send_nrc(service_id, UDS_NRC_REQUEST_OUT_OF_RANGE);
          break;
        }
        g_firmware_type = data[3];

        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        safe_mode_send_response(resp, 3);
      }
      else if (did == 0xF18CU)
      {
        uint8_t sn32[32];
        uint16_t n;
        uint16_t i;

        n = (uint16_t)(len - 3U);
        if (n > 32U)
        {
          safe_mode_send_nrc(service_id, UDS_NRC_REQUEST_OUT_OF_RANGE);
          break;
        }
        memset(sn32, 0x20, 32U);
        for (i = 0U; i < n; i++)
        {
          sn32[i] = data[3U + i];
        }
        if (device_info_write_sn(sn32) != 0)
        {
          safe_mode_send_nrc(service_id, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
          break;
        }
        /* Flash erase/program stalls the CPU; recover CAN before 6E / later 22 F18C. */
        (void)safe_mode_can_busoff_recover();
        (void)sit1145_normal_mode_set();
        (void)can_driver_wait_tx_idle(50U);
        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        safe_mode_send_response(resp, 3);
        (void)can_driver_wait_tx_idle(50U);
      }
      else
      {
        safe_mode_send_nrc(service_id, UDS_NRC_REQUEST_OUT_OF_RANGE);
      }
      break;
    }

    case UDS_ROUTINE_CONTROL:
    {
      uint16_t routine_id;

      /* programming session check */
      if (g_current_session != SESSION_PROGRAMMING)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_CONDITIONS_NOT_CORRECT);
        break;
      }

      /* security gate */
      if (!g_security_unlocked)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_SECURITY_ACCESS_DENIED);
        break;
      }

      if (len < 4U)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_INCORRECT_MSG_LENGTH);
        break;
      }

      routine_id = ((uint16_t)data[2] << 8) | (uint16_t)data[3];

      if (data[1] == 0x01U && routine_id == 0xFF00U)
      {
        /* erase the inactive slot (never the running image's last-known slot) */
        dl_bind_slot(select_inactive_slot());
        safe_mode_begin_long_op(service_id);

        if (erase_slot_flash(g_dl_slot) != 0U)
        {
          safe_mode_end_long_op();
          safe_mode_send_nrc(service_id, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
          break;
        }

        g_dl_erased = 1;
        g_meta.ota_state    = OTA_STATE_DOWNLOADING;
        g_meta.pending_slot = g_dl_slot;
        if (g_dl_slot == SLOT_A)
        {
          g_meta.slot_a_valid = 0U;
        }
        else
        {
          g_meta.slot_b_valid = 0U;
        }
        (void)boot_metadata_save(&g_meta);

        dl_reset_state();

        /* Flash erase may have caused bus-off; recover before responding */
        (void)safe_mode_can_busoff_recover();
        (void)sit1145_normal_mode_set();
        (void)can_driver_wait_tx_idle(20U);
        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        resp[3] = data[3];
        safe_mode_send_response(resp, 4);
        (void)can_driver_wait_tx_idle(50U);
      }
      else if (data[1] == 0x01U && routine_id == 0xFF01U)
      {
        /* checkProgrammingDependencies: verify programmed slot */
        safe_mode_begin_long_op(service_id);
        if (boot_verify_image(g_dl_slot_base, g_dl_slot_size) != 0)
        {
          safe_mode_end_long_op();
          safe_mode_send_nrc(service_id, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
          break;
        }
        safe_mode_end_long_op();
        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = data[1];
        resp[2] = data[2];
        resp[3] = data[3];
        safe_mode_send_response(resp, 4);
      }
      else
      {
        safe_mode_send_nrc(service_id, UDS_NRC_REQUEST_OUT_OF_RANGE);
      }
      break;
    }

    case UDS_REQUEST_DOWNLOAD:
    {
      /* programming session check */
      if (g_current_session != SESSION_PROGRAMMING)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_CONDITIONS_NOT_CORRECT);
        break;
      }

      /* security gate: require SecurityAccess (0x27) to be unlocked */
      if (!g_security_unlocked)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_SECURITY_ACCESS_DENIED);
        break;
      }

      if (g_dl_erased == 0U)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_REQUEST_SEQUENCE_ERROR);
        break;
      }

      /* parse dataFormatIdentifier + addressAndLengthFormatIdentifier */
      if (len >= 3U)
      {
        uint8_t alfid  = data[2];
        uint8_t addr_n = (uint8_t)((alfid >> 4) & 0x0FU);
        uint8_t size_n = (uint8_t)(alfid & 0x0FU);
        uint32_t mem_size;

        if ((addr_n == 0U) || (size_n == 0U) ||
            (addr_n > 4U) || (size_n > 4U) ||
            (len < (uint16_t)(3U + addr_n + size_n)))
        {
          safe_mode_send_nrc(service_id, UDS_NRC_INCORRECT_MSG_LENGTH);
          break;
        }

        mem_size = uds_parse_be(&data[3U + addr_n], size_n);
        if ((mem_size == 0U) || (mem_size > g_dl_slot_size))
        {
          safe_mode_send_nrc(service_id, UDS_NRC_REQUEST_OUT_OF_RANGE);
          break;
        }
        g_dl_expected_size = mem_size;
      }
      else
      {
        g_dl_expected_size = 0;
      }

      dl_reset_state();

      /* LFI 0x20 = 2-byte maxNumberOfBlockLength (one TransferData request) */
      resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
      resp[1] = 0x20;
      resp[2] = (uint8_t)((UDS_MAX_BLOCK_LEN >> 8) & 0xFFU);
      resp[3] = (uint8_t)(UDS_MAX_BLOCK_LEN & 0xFFU);
      safe_mode_send_response(resp, 4);
      break;
    }

    case UDS_TRANSFER_DATA:
    {
      uint8_t block_seq;
      uint16_t data_len;

      if (g_current_session != SESSION_PROGRAMMING)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_CONDITIONS_NOT_CORRECT);
        break;
      }

      if (!g_dl_active)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_REQUEST_SEQUENCE_ERROR);
        break;
      }

      if (!g_security_unlocked)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_SECURITY_ACCESS_DENIED);
        break;
      }

      if (len < 3U)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_INCORRECT_MSG_LENGTH);
        break;
      }

      block_seq = data[1];
      /* ISO 14229: same BSC as last success → replay 76, do not write again. */
      if ((g_dl_block_seq != 0U) && (block_seq == g_dl_block_seq))
      {
        resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
        resp[1] = block_seq;
        safe_mode_send_response(resp, 2);
        break;
      }
      g_dl_block_seq++;
      if (g_dl_block_seq == 0x00U)
      {
        g_dl_block_seq = 0x01U;
      }
      if (block_seq != g_dl_block_seq)
      {
        g_dl_active = 0;
        safe_mode_send_nrc(service_id, UDS_NRC_WRONG_BLOCK_SEQUENCE);
        break;
      }

      data_len = (uint16_t)(len - 2U);

      if ((g_dl_bytes_written + g_dl_pad_len + (uint32_t)data_len) >
          g_dl_slot_size)
      {
        g_dl_active = 0;
        safe_mode_send_nrc(service_id, UDS_NRC_TRANSFER_DATA_ABORTED);
        break;
      }

      flash_unlock();
      if (program_image_bytes(&data[2], data_len) != 0U)
      {
        g_dl_active = 0;
        flash_lock();
        safe_mode_send_nrc(service_id, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
        break;
      }
      flash_lock();

      resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
      resp[1] = block_seq;
      safe_mode_send_response(resp, 2);
      (void)can_driver_wait_tx_idle(20U);
      break;
    }

    case UDS_REQUEST_TRANSFER_EXIT:
    {
      /* Do not CRC/SHA/ECDSA here. This runs inside can_driver_poll -> ISO-TP.
       * Blocking kills SIT1145 keepalive; 77 is then sent with the transceiver
       * already out of Normal and the host only sees SID=0x37 timeout. */
      if (g_current_session != SESSION_PROGRAMMING)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_CONDITIONS_NOT_CORRECT);
        break;
      }

      if ((g_xfer_exit_pending != 0U) || (g_xfer_exit_busy != 0U))
      {
        safe_mode_send_pending(service_id);
        break;
      }

      if (!g_dl_active)
      {
        if ((g_meta.trial_state == TRIAL_STATE_PENDING) &&
            (g_meta.trial_slot == g_dl_slot))
        {
          resp[0] = service_id + UDS_POSITIVE_RESPONSE_OFFSET;
          safe_mode_send_response(resp, 1);
        }
        else
        {
          safe_mode_send_nrc(service_id, UDS_NRC_TRANSFER_DATA_ABORTED);
        }
        break;
      }

      if (!g_security_unlocked)
      {
        safe_mode_send_nrc(service_id, UDS_NRC_SECURITY_ACCESS_DENIED);
        break;
      }

      g_dl_active = 0;
      g_long_op_sid = UDS_REQUEST_TRANSFER_EXIT;
      g_long_op_last_78_ms = timer_get_tick();
      /* Flash writes may have left CAN in bus-off; recover before sending 0x78 */
      (void)safe_mode_can_busoff_recover();
      (void)sit1145_normal_mode_set();
      safe_mode_send_pending(service_id);
      (void)can_driver_wait_tx_idle(50U);
      g_xfer_exit_busy = 1U;
      g_xfer_exit_pending = 1U;
      break;
    }

    default:
      /* unsupported service */
      safe_mode_send_nrc(service_id, UDS_NRC_SERVICE_NOT_SUPPORTED);
      break;
  }
}

/**
 * @brief  ISO-TP message received callback
 * @note   called by isotp_rx_process when a complete UDS message is reassembled
 * @param  data: pointer to complete UDS payload (PCI already stripped)
 * @param  len:  payload length in bytes
 * @retval none
 */
static void isotp_message_received(uint8_t *data, uint16_t len)
{
  uds_process_message(data, len);
}

static void safe_mode_finish_security_verify(void)
{
  uint8_t resp[2];

  g_sa_verify_busy = 1U;
  g_long_op_sid = UDS_SECURITY_ACCESS;
  g_long_op_last_78_ms = timer_get_tick();
  uECC_set_progress_cb(safe_mode_long_op_pump);
  g_s3_last_ms = timer_get_tick();
  (void)sit1145_normal_mode_set();
  safe_mode_long_op_pump();

  if (verify_security_ecdsa_signature() != 0U)
  {
    uECC_set_progress_cb((void (*)(void))0);
    g_security_unlocked = 1;
    g_security_fail_count = 0;
    g_seed_generated = 0;
    g_sa_sig_bytes_received = 0;
    g_sa_verify_busy = 0U;
    g_sa_verify_pending = 0U;
    (void)safe_mode_can_busoff_recover();
    (void)sit1145_normal_mode_set();
    (void)can_driver_wait_tx_idle(50U);
    resp[0] = (uint8_t)(UDS_SECURITY_ACCESS + UDS_POSITIVE_RESPONSE_OFFSET);
    resp[1] = 0x02U;
    safe_mode_send_response(resp, 2);
    (void)can_driver_wait_tx_idle(50U);
    g_s3_last_ms = timer_get_tick();
    return;
  }

  uECC_set_progress_cb((void (*)(void))0);
  g_security_unlocked = 0;
  g_security_fail_count++;
  g_seed_generated = 0;
  g_sa_sig_bytes_received = 0;
  g_sa_verify_busy = 0U;
  g_sa_verify_pending = 0U;
  (void)safe_mode_can_busoff_recover();
  (void)sit1145_normal_mode_set();
  if (g_security_fail_count >= SECURITY_MAX_FAILURES)
  {
    g_security_lockout_until_ms = timer_get_tick() + SECURITY_LOCKOUT_MS;
    safe_mode_send_nrc(UDS_SECURITY_ACCESS, UDS_NRC_EXCEEDED_NUMBER_OF_ATTEMPTS);
  }
  else
  {
    safe_mode_send_nrc(UDS_SECURITY_ACCESS, UDS_NRC_INVALID_KEY);
  }
  (void)can_driver_wait_tx_idle(50U);
  g_s3_last_ms = timer_get_tick();
}

static void safe_mode_xfer_exit_abort(uint8_t nrc)
{
  uECC_set_progress_cb((void (*)(void))0);
  boot_verify_set_progress_cb((void (*)(void))0);
  g_xfer_exit_busy = 0U;
  g_xfer_exit_pending = 0U;
  (void)sit1145_normal_mode_set();
  safe_mode_send_nrc(UDS_REQUEST_TRANSFER_EXIT, nrc);
}

static void safe_mode_finish_transfer_exit(void)
{
  uint8_t resp[4];
  uint32_t computed_crc;

  g_xfer_exit_busy = 1U;
  g_long_op_sid = UDS_REQUEST_TRANSFER_EXIT;
  g_long_op_last_78_ms = timer_get_tick();
  uECC_set_progress_cb(safe_mode_long_op_pump);
  boot_verify_set_progress_cb(safe_mode_long_op_pump);

  g_s3_last_ms = timer_get_tick();
  (void)sit1145_normal_mode_set();
  safe_mode_long_op_pump();

  flash_unlock();
  if (program_image_flush() != 0U)
  {
    flash_lock();
    safe_mode_xfer_exit_abort(UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
    return;
  }
  flash_lock();
  safe_mode_long_op_pump();

  if (g_dl_bytes_written < IMAGE_HEADER_SIZE)
  {
    safe_mode_xfer_exit_abort(UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
    return;
  }

  if ((g_dl_expected_size != 0U) && (g_dl_bytes_written != g_dl_expected_size))
  {
    safe_mode_xfer_exit_abort(UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
    return;
  }

  g_dl_erased = 0;

  if (boot_verify_image(g_dl_slot_base, g_dl_slot_size) != 0)
  {
    safe_mode_xfer_exit_abort(UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
    return;
  }

  {
    const image_header_t *hdr = boot_verify_get_header(g_dl_slot_base);
    computed_crc = hdr->crc32;
  }

  if (g_dl_slot == SLOT_A)
  {
    g_meta.slot_a_valid = 1;
    g_meta.slot_a_crc32 = computed_crc;
  }
  else
  {
    g_meta.slot_b_valid = 1;
    g_meta.slot_b_crc32 = computed_crc;
  }
  g_meta.ota_state         = OTA_STATE_IDLE;
  g_meta.pending_slot      = g_dl_slot;
  g_meta.trial_state       = TRIAL_STATE_PENDING;
  g_meta.trial_slot        = g_dl_slot;
  g_meta.trial_retry_count = 0;
  if (g_meta.trial_max_retries == 0U)
  {
    g_meta.trial_max_retries = TRIAL_MAX_RETRIES;
  }

  safe_mode_long_op_pump();
  if (boot_metadata_save(&g_meta) != 0)
  {
    safe_mode_xfer_exit_abort(UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
    return;
  }

  uECC_set_progress_cb((void (*)(void))0);
  boot_verify_set_progress_cb((void (*)(void))0);

  /* Flash programming stalls the CPU; CAN may be bus-off. Recover, refresh
   * P2* with 0x78, then retry 0x77. Host timeout is otherwise intermittent. */
  {
    uint8_t n;
    /* Metadata save stalls CPU; CAN is bus-off. Full recovery + non-blocking 0x77. */
    (void)safe_mode_can_busoff_recover();
    (void)sit1145_normal_mode_set();
    (void)can_driver_wait_tx_idle(20U);
    safe_mode_send_pending(UDS_REQUEST_TRANSFER_EXIT);
    (void)can_driver_wait_tx_idle(10U);
    /* Non-blocking 0x77: build ISO-TP SF and send via can_driver_send directly.
     * isotp_tx_send blocks 1s on bus-off; that kills the main loop. */
    {
      uint8_t sf77[8];
      uint8_t attempt;
      sf77[0] = 0x01U;  /* ISO-TP SF, DLC=1 */
      sf77[1] = (uint8_t)(UDS_REQUEST_TRANSFER_EXIT + UDS_POSITIVE_RESPONSE_OFFSET);
      sf77[2] = 0xCCU; sf77[3] = 0xCCU; sf77[4] = 0xCCU;
      sf77[5] = 0xCCU; sf77[6] = 0xCCU; sf77[7] = 0xCCU;
      for (attempt = 0U; attempt < 10U; attempt++)
      {
        if (can_driver_send(SAFE_MODE_CAN_ID_RESPONSE, sf77, 8) == 0)
        {
          (void)can_driver_wait_tx_idle(20U);
          break;
        }
        /* bus-off: recover and retry */
        (void)safe_mode_can_busoff_recover();
        (void)sit1145_normal_mode_set();
      }
    }
  }
  g_xfer_exit_busy = 0U;
  g_xfer_exit_pending = 0U;
  g_s3_last_ms = timer_get_tick();
}

#if (SAFE_MODE_PROBE_ENABLE != 0U)
static void safe_mode_probe_poll(void)
{
  uint8_t data[8];
  uint32_t now = timer_get_tick();

  if ((now - g_probe_last_ms) < SAFE_MODE_PROBE_PERIOD_MS)
  {
    return;
  }
  g_probe_last_ms = now;

  data[0] = SAFE_MODE_PROBE_STATE;
  data[1] = 0x42U;  /* 'B' = Boot identity marker, distinguish from APP (0x41) */
  data[2] = 0x07U;
  data[3] = 0x00U;
  data[4] = 0x00U;
  data[5] = 0x00U;
  data[6] = 0x00U;
  data[7] = 0x00U;
  (void)can_driver_send(CAN_ID_LIFECYCLE_BROADCAST, data, 8);
}
#endif

#if (SAFE_MODE_SIT1145_KEEPALIVE_ENABLE != 0U)
static void safe_mode_sit1145_keepalive_poll(void)
{
  uint32_t now = timer_get_tick();

  if ((now - g_sit1145_keepalive_last_ms) < SAFE_MODE_SIT1145_KEEPALIVE_PERIOD_MS)
  {
    return;
  }
  g_sit1145_keepalive_last_ms = now;
  (void)sit1145_normal_mode_set();
}
#endif

/**
 * @brief  CAN RX handler for safe mode: ID filtering + ISO-TP dispatch
 * @param  id:   CAN frame ID
 * @param  data: pointer to frame data
 * @param  len:  frame data length
 * @retval none
 */
static void safe_mode_can_rx_handler(uint32_t id, uint8_t *data, uint8_t len)
{
  /* accept physical addressing (0x18DA0D03) and functional addressing (0x18DB33xx) */
  if (id == SAFE_MODE_CAN_ID_REQUEST ||
      (id & 0x1FFFFF00U) == 0x18DB3300U)
  {
    isotp_rx_process(data, len);
  }
}

/* exported functions --------------------------------------------------------*/

/**
 * @brief  enter safe mode: initialize CAN and wait for OTA download
 * @note   DOWNLOADING, no bootable slot, or both verifies failed.
 * @param  none
 * @retval none (does not return)
 */
void enter_safe_mode(void)
{
  g_safe_mode = 1;
  g_current_session = SESSION_DEFAULT;

  /* initialize CAN for safe mode communication (SIT1145 in Normal from init) */
  can_driver_init();
  can_driver_register_rx_callback(safe_mode_can_rx_handler);

  /* initialize ISO-TP receiver with UDS message callback */
  isotp_init(isotp_message_received);
  g_s3_last_ms = timer_get_tick();
#if (SAFE_MODE_PROBE_ENABLE != 0U)
  g_probe_last_ms = 0;
#endif
#if (SAFE_MODE_SIT1145_KEEPALIVE_ENABLE != 0U)
  g_sit1145_keepalive_last_ms = 0;
#endif

  /* safe mode event loop */
  while (1)
  {
    timer_poll();
    can_driver_poll();
    isotp_poll();
#if (SAFE_MODE_PROBE_ENABLE != 0U)
    safe_mode_probe_poll();
#endif
#if (SAFE_MODE_SIT1145_KEEPALIVE_ENABLE != 0U)
    safe_mode_sit1145_keepalive_poll();
#endif

    if (g_sa_verify_pending != 0U)
    {
      g_sa_verify_pending = 0U;
      safe_mode_finish_security_verify();
    }

    if (g_xfer_exit_pending != 0U)
    {
      g_xfer_exit_pending = 0U;
      safe_mode_finish_transfer_exit();
    }

    if ((g_xfer_exit_busy == 0U) &&
        (g_sa_verify_busy == 0U) &&
        (g_current_session != SESSION_DEFAULT) &&
        ((timer_get_tick() - g_s3_last_ms) >= UDS_S3_TIMEOUT_MS))
    {
      g_current_session = SESSION_DEFAULT;
      g_security_unlocked = 0;
      dl_abort();
      if ((g_meta.slot_a_valid != 0U) || (g_meta.slot_b_valid != 0U))
      {
        g_meta.ota_state = OTA_STATE_IDLE;
        (void)boot_metadata_save(&g_meta);
      }
    }
  }
}
