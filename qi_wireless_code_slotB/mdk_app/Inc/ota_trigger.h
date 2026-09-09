/**
  **************************************************************************
  * @file     ota_trigger.h
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

/* define to prevent recursive inclusion -------------------------------------*/
#ifndef __OTA_TRIGGER_H
#define __OTA_TRIGGER_H

#ifdef __cplusplus
extern "C" {
#endif

/* includes ------------------------------------------------------------------*/
#include "at32f422_426.h"

/* exported constants --------------------------------------------------------*/

/** @brief  Flash layout constants (must match bootloader / docs Flash layout) */
#define OTA_APP_A_BASE_ADDR     0x08007000U   /*!< application A start address */
#define OTA_APP_A_SIZE          0xA800U       /*!< application A size: 42KB */
#define OTA_APP_B_BASE_ADDR     0x08011800U   /*!< application B start address */
#define OTA_APP_B_SIZE          0xA800U       /*!< application B size: 42KB */
#define OTA_META_PRIMARY_ADDR   0x0801C000U   /*!< primary metadata (2KB sector) */
#define OTA_META_BACKUP_ADDR    0x0801C800U   /*!< backup metadata (next 2KB sector) */
#define OTA_META_PAGE_SIZE      0x800U        /*!< metadata erase size: 1 sector */
#define OTA_IMAGE_HEADER_SIZE   256U          /*!< image header size in bytes */

/** @brief  OTA metadata magic and version */
#define OTA_META_MAGIC          0x4F54414DU   /*!< "MATO" */
#define OTA_META_VERSION        1U            /*!< metadata format version */

/** @brief  Slot definitions */
#define OTA_SLOT_NONE           0xFEU         /*!< no pending slot */
#define OTA_SLOT_A              0U            /*!< slot A index */
#define OTA_SLOT_B              1U            /*!< slot B index */

/** @brief  OTA state codes */
#define OTA_STATE_IDLE          0x00U         /*!< no OTA in progress */
#define OTA_STATE_DOWNLOADING   0x01U         /*!< OTA download in progress */

/** @brief  trial-boot state (metadata.trial_state) */
#define TRIAL_STATE_IDLE        0U
#define TRIAL_STATE_PENDING     1U
#define TRIAL_STATE_ACTIVE      2U
#define TRIAL_STATE_CONFIRMED   3U

/** @brief  Flash sector size for AT32F426 */
#define OTA_FLASH_SECTOR_SIZE   0x800U        /*!< 2KB per sector */

/** @brief  image header magic "XATO" (must match bootloader IMAGE_MAGIC) */
#define OTA_IMAGE_MAGIC         0x4F544158U

/* exported types ------------------------------------------------------------*/

/**
 * @brief  application image header (256 bytes) at each slot base
 * @note   must match bootloader image_header_t
 */
typedef struct
{
  uint32_t magic;              /*!< 0x4F544158 "XATO" */
  uint32_t image_length;       /*!< firmware size excluding this header */
  uint32_t crc32;              /*!< CRC32 of firmware (excluding header) */
  uint8_t  signature[64];      /*!< ECDSA P-256 R||S */
  char     version[16];        /*!< "MAJOR.MINOR.PATCH\0" */
  uint32_t build_timestamp;    /*!< Unix timestamp */
  uint8_t  reserved[160];      /*!< pad to 256 bytes */
} ota_image_header_t;

/**
 * @brief  OTA metadata structure (must match bootloader definition)
 * @note   stored in Flash at OTA_META_PRIMARY_ADDR and OTA_META_BACKUP_ADDR.
 *         CRC32 covers all fields except the trailing crc32 field itself.
 */
typedef struct
{
  uint32_t magic;               /*!< 0x4F54414D "MATO" */
  uint32_t version;             /*!< metadata format version = 1 */
  uint8_t  active_slot;         /*!< 0=A, 1=B */
  uint8_t  pending_slot;        /*!< 0=A, 1=B, 0xFE=none */
  uint8_t  slot_a_valid;        /*!< 1=slot A image valid */
  uint8_t  slot_b_valid;        /*!< 1=slot B image valid */
  uint32_t slot_a_crc32;        /*!< CRC32 of slot A image */
  uint32_t slot_b_crc32;        /*!< CRC32 of slot B image */
  uint8_t  trial_state;         /*!< 0=IDLE, 1=PENDING, 2=ACTIVE, 3=CONFIRMED */
  uint8_t  trial_slot;          /*!< slot under trial (0=A, 1=B) */
  uint8_t  trial_retry_count;   /*!< current retry count */
  uint8_t  trial_max_retries;   /*!< max retries (default 3) */
  uint16_t trial_timeout_sec;   /*!< trial timeout in seconds (default 10) */
  uint16_t reserved1;           /*!< reserved for alignment */
  uint32_t rollback_count;      /*!< number of rollbacks performed */
  uint8_t  last_boot_reason;    /*!< 0x00=POR, 0x01=SW, 0x02=WDG, 0x03=OTA, 0x04=rollback */
  uint8_t  ota_state;           /*!< 0x00=idle, 0x01=downloading */
  uint8_t  reserved2[2];        /*!< reserved */
  uint8_t  padding[232];        /*!< padding to 272 bytes (must match bootloader) */
  uint32_t crc32;               /*!< CRC32 of all above fields */
} ota_metadata_t;

/* exported functions -------------------------------------------------------*/

/**
 * @brief  trigger OTA upgrade mode
 * @note   reads current metadata from primary flash, sets ota_state to
 *         OTA_STATE_DOWNLOADING, saves metadata, then performs a system reset.
 *         this function does NOT return.
 * @param  none
 * @retval none (does not return)
 */
void ota_trigger_request(void);

/**
 * @brief  write OTA_STATE_DOWNLOADING to metadata without resetting
 * @retval 0 on success, -1 on flash write error
 */
int8_t ota_trigger_prepare(void);

/**
 * @brief  read metadata from primary flash location
 * @param  meta: pointer to metadata structure to fill
 * @retval 0 on success (valid metadata), -1 on failure (invalid or read error)
 */
int8_t ota_metadata_read(ota_metadata_t *meta);

/**
 * @brief  save metadata to backup then primary (power-loss safe)
 * @param  meta: pointer to metadata structure to save
 * @retval 0 on success, -1 on flash write error
 */
int8_t ota_metadata_save(const ota_metadata_t *meta);

/**
 * @brief  start trial-boot window after APP init
 * @note   if metadata trial_state is ACTIVE, APP must confirm within
 *         trial_timeout_sec or it resets so bootloader can roll back.
 */
void ota_trial_init(void);

/**
 * @brief  poll trial confirm / timeout (call from main loop)
 */
void ota_trial_poll(void);

/**
 * @brief  compute CRC32 (IEEE 802.3, polynomial 0xEDB88320)
 * @param  data: pointer to data
 * @param  length: number of bytes
 * @retval CRC32 value
 */
uint32_t ota_crc32(const void *data, uint32_t length);

/**
 * @brief  slot this APP image is running from (0=A, 1=B), derived from VTOR
 */
uint8_t ota_running_slot(void);

/**
 * @brief  Flash base of the running slot (header address)
 */
uint32_t ota_running_slot_base(void);

/**
 * @brief  copy version string from this slot's image header
 * @param  out: destination buffer
 * @param  out_len: capacity including NUL
 * @retval 0 on success, -1 if header magic is invalid
 */
int8_t ota_get_image_version(char *out, uint8_t out_len);

#ifdef __cplusplus
}
#endif

#endif /* __OTA_TRIGGER_H */
