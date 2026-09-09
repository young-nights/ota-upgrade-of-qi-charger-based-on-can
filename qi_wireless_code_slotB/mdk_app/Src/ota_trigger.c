/**
  **************************************************************************
  * @file     ota_trigger.c
  * @brief    OTA trigger module for APP firmware
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
#include "ota_trigger.h"
#include "timer_drv.h"
#include "at32f422_426_conf.h"
#include <string.h>

/* private define ------------------------------------------------------------*/

/** @brief  byte offset of the crc32 field within ota_metadata_t */
#define META_CRC32_OFFSET    (sizeof(ota_metadata_t) - sizeof(uint32_t))

/* private functions ---------------------------------------------------------*/

/**
 * @brief  validate metadata structure: check magic, version, and CRC32
 * @param  meta: pointer to metadata to validate
 * @retval 0 if valid, -1 if invalid
 */
static int8_t meta_validate(const ota_metadata_t *meta)
{
  uint32_t computed_crc;
  uint32_t stored_crc;

  if (meta->magic != OTA_META_MAGIC)
  {
    return -1;
  }

  if (meta->version != OTA_META_VERSION)
  {
    return -1;
  }

  stored_crc   = meta->crc32;
  computed_crc = ota_crc32((const void *)meta, META_CRC32_OFFSET);

  if (computed_crc != stored_crc)
  {
    return -1;
  }

  return 0;
}

/**
 * @brief  fill metadata with default values
 * @param  meta: pointer to metadata to initialize
 * @retval none
 */
static void meta_fill_defaults(ota_metadata_t *meta)
{
  memset((void *)meta, 0, sizeof(ota_metadata_t));

  meta->magic             = OTA_META_MAGIC;
  meta->version           = OTA_META_VERSION;
  meta->active_slot       = OTA_SLOT_A;
  meta->pending_slot      = OTA_SLOT_NONE;
  meta->slot_a_valid      = 0;
  meta->slot_b_valid      = 0;
  meta->slot_a_crc32      = 0;
  meta->slot_b_crc32      = 0;
  meta->trial_state       = 0;  /* TRIAL_STATE_IDLE */
  meta->trial_slot        = OTA_SLOT_A;
  meta->trial_retry_count = 0;
  meta->trial_max_retries = 3;
  meta->trial_timeout_sec = 10;
  meta->reserved1         = 0;
  meta->rollback_count    = 0;
  meta->last_boot_reason  = 0;
  meta->ota_state         = OTA_STATE_IDLE;
}

/* exported functions --------------------------------------------------------*/

/**
 * @brief  compute CRC32 (IEEE 802.3, polynomial 0xEDB88320)
 * @param  data: pointer to data
 * @param  length: number of bytes
 * @retval CRC32 value
 */
uint32_t ota_crc32(const void *data, uint32_t length)
{
  const uint8_t *p = (const uint8_t *)data;
  uint32_t crc = 0xFFFFFFFFU;
  uint32_t i;
  uint32_t j;
  uint32_t bit;

  for (i = 0; i < length; i++)
  {
    crc ^= (uint32_t)p[i];
    for (j = 0; j < 8; j++)
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

/**
 * @brief  read metadata from primary flash location
 * @param  meta: pointer to metadata structure to fill
 * @retval 0 on success (valid metadata), -1 on failure (invalid or read error)
 */
int8_t ota_metadata_read(ota_metadata_t *meta)
{
  const ota_metadata_t *primary;
  const ota_metadata_t *backup;

  /* try primary metadata */
  primary = (const ota_metadata_t *)OTA_META_PRIMARY_ADDR;
  if (meta_validate(primary) == 0)
  {
    memcpy((void *)meta, (const void *)primary, sizeof(ota_metadata_t));
    return 0;
  }

  /* primary invalid, try backup */
  backup = (const ota_metadata_t *)OTA_META_BACKUP_ADDR;
  if (meta_validate(backup) == 0)
  {
    memcpy((void *)meta, (const void *)backup, sizeof(ota_metadata_t));
    return 0;
  }

  /* both invalid, use defaults */
  meta_fill_defaults(meta);
  return -1;
}

static int8_t meta_write_to_flash(uint32_t addr, const ota_metadata_t *meta)
{
  const uint32_t *src;
  uint32_t words;
  uint32_t i;
  flash_status_type status;

  flash_unlock();
  status = flash_sector_erase(addr);
  if (status != FLASH_OPERATE_DONE)
  {
    flash_lock();
    return -1;
  }

  src   = (const uint32_t *)meta;
  words = sizeof(ota_metadata_t) / sizeof(uint32_t);
  for (i = 0; i < words; i++)
  {
    status = flash_word_program(addr + (i * 4U), src[i]);
    if (status != FLASH_OPERATE_DONE)
    {
      flash_lock();
      return -1;
    }
  }
  flash_lock();
  return 0;
}

/**
 * @brief  save metadata to backup then primary Flash
 */
int8_t ota_metadata_save(const ota_metadata_t *meta)
{
  ota_metadata_t meta_copy;

  memcpy((void *)&meta_copy, (const void *)meta, sizeof(ota_metadata_t));
  meta_copy.crc32 = ota_crc32((const void *)&meta_copy, META_CRC32_OFFSET);

  if (meta_write_to_flash(OTA_META_BACKUP_ADDR, &meta_copy) != 0)
  {
    return -1;
  }
  if (meta_write_to_flash(OTA_META_PRIMARY_ADDR, &meta_copy) != 0)
  {
    return -1;
  }

  return 0;
}

uint32_t ota_running_slot_base(void)
{
  uint32_t vtor = SCB->VTOR;

  /* VTOR is the code entry (header+256), not the slot header. */
  if (vtor >= (OTA_APP_B_BASE_ADDR + OTA_IMAGE_HEADER_SIZE))
  {
    return OTA_APP_B_BASE_ADDR;
  }
  return OTA_APP_A_BASE_ADDR;
}

uint8_t ota_running_slot(void)
{
  if (ota_running_slot_base() == OTA_APP_B_BASE_ADDR)
  {
    return OTA_SLOT_B;
  }
  return OTA_SLOT_A;
}

int8_t ota_get_image_version(char *out, uint8_t out_len)
{
  const ota_image_header_t *hdr;
  uint8_t i;

  if ((out == (char *)0) || (out_len == 0U))
  {
    return -1;
  }

  hdr = (const ota_image_header_t *)(uintptr_t)ota_running_slot_base();
  if (hdr->magic != OTA_IMAGE_MAGIC)
  {
    return -1;
  }

  for (i = 0U; (i < 15U) && (i < (out_len - 1U)); i++)
  {
    char c = hdr->version[i];
    if (c == '\0')
    {
      break;
    }
    out[i] = c;
  }
  out[i] = '\0';
  return 0;
}

int8_t ota_trigger_prepare(void)
{
  ota_metadata_t meta;
  uint8_t slot;

  if (ota_metadata_read(&meta) != 0)
  {
    /* keep the running slot marked valid so Bootloader does not treat this
     * APP as missing when metadata was never written or both copies failed */
    slot = ota_running_slot();
    meta_fill_defaults(&meta);
    meta.active_slot = slot;
    if (slot == OTA_SLOT_B)
    {
      meta.slot_b_valid = 1U;
    }
    else
    {
      meta.slot_a_valid = 1U;
    }
  }

  meta.ota_state = OTA_STATE_DOWNLOADING;
  return ota_metadata_save(&meta);
}

/**
 * @brief  trigger OTA upgrade mode
 * @note   sets ota_state to DOWNLOADING, saves metadata, then resets.
 *         does not return on success.
 */
void ota_trigger_request(void)
{
  (void)ota_trigger_prepare();
  NVIC_SystemReset();
}

#define TRIAL_HEALTH_DELAY_MS   100U

static uint8_t  g_trial_pending = 0;
static uint32_t g_trial_start_ms = 0;
static uint32_t g_trial_deadline_ms = 0;

static int8_t ota_confirm_trial(void)
{
  ota_metadata_t meta;

  if (ota_metadata_read(&meta) != 0)
  {
    return -1;
  }
  if (meta.trial_state != TRIAL_STATE_ACTIVE)
  {
    return 0;
  }

  /* only confirm if this image is the one under trial */
  if (meta.trial_slot != ota_running_slot())
  {
    return -1;
  }

  meta.active_slot  = meta.trial_slot;
  meta.pending_slot = OTA_SLOT_NONE;
  meta.trial_state  = TRIAL_STATE_CONFIRMED;
  meta.trial_retry_count = 0;
  meta.ota_state    = OTA_STATE_IDLE;

  return ota_metadata_save(&meta);
}

void ota_trial_init(void)
{
  ota_metadata_t meta;
  uint32_t timeout_ms;

  g_trial_pending = 0;
  if (ota_metadata_read(&meta) != 0)
  {
    return;
  }
  if (meta.trial_state != TRIAL_STATE_ACTIVE)
  {
    return;
  }
  if (meta.trial_slot != ota_running_slot())
  {
    return;
  }

  timeout_ms = (uint32_t)meta.trial_timeout_sec * 1000U;
  if (timeout_ms == 0U)
  {
    timeout_ms = 10000U;
  }

  g_trial_pending     = 1;
  g_trial_start_ms    = timer_get_tick();
  g_trial_deadline_ms = g_trial_start_ms + timeout_ms;
}

void ota_trial_poll(void)
{
  uint32_t now;

  if (g_trial_pending == 0U)
  {
    return;
  }

  now = timer_get_tick();
  if ((int32_t)(now - g_trial_deadline_ms) >= 0)
  {
    /* trial timed out: reset into bootloader to roll back */
    NVIC_SystemReset();
  }

  /* confirm only after core init has been up for a short health window */
  if ((now - g_trial_start_ms) >= TRIAL_HEALTH_DELAY_MS)
  {
    if (ota_confirm_trial() == 0)
    {
      g_trial_pending = 0;
    }
  }
}
