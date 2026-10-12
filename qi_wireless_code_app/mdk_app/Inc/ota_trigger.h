/**
 **************************************************************************
 * @file     ota_trigger.h
 * @brief    OTA metadata + constants for APP (Boot + App architecture)
 **************************************************************************
 *
 * Single-App OTA (OTA-ARCH-0920): A/B slots removed. APP receives
 * firmware into the Backup region via UDS, verifies it, flags metadata
 * (backup_valid) and resets; BOOT performs the physical copy.
 *
 * Flash layout (must match bootloader boot_metadata.h — same-source):
 *   Boot     0x08000000..0x08003FFF  16KB
 *   App      0x08004000..0x0800FFFF  48KB (XATO header @0x08004000,
 *            code entry @0x08004100 = Keil IROM1 0x08004100,0xBF00)
 *   Backup   0x08010000..0x0801BFFF  48KB
 *   Metadata 0x0801C000 / 0x0801C800, DeviceInfo 0x0801D000, NVM 0x0801E000
 *
 * ota_metadata_t v4 (28B, crc32 @0x18, META_VERSION=4),
 * identical across projects; v2-and-earlier data rejected -> defaults.
 */

#ifndef __OTA_TRIGGER_H
#define __OTA_TRIGGER_H

#ifdef __cplusplus
extern "C" {
#endif

#include "at32f422_426.h"

/* exported constants ------------------------------------------------------ */

#define OTA_BOOT_BASE_ADDR      0x08000000U
#define OTA_BOOT_SIZE           0x4000U
#define OTA_APP_BASE_ADDR       0x08004000U   /* App image region */
#define OTA_APP_SIZE            0xC000U       /* 48KB */
#define OTA_BACKUP_BASE_ADDR    0x08010000U   /* staging region */
#define OTA_BACKUP_SIZE         0xC000U       /* 48KB */
#define OTA_APP_ENTRY_ADDR      (OTA_APP_BASE_ADDR + OTA_IMAGE_HEADER_SIZE)

#define OTA_META_PRIMARY_ADDR   0x0801C000U
#define OTA_META_BACKUP_ADDR    0x0801C800U
#define OTA_META_PAGE_SIZE      0x400U
#define OTA_IMAGE_HEADER_SIZE   256U
#define OTA_FLASH_SECTOR_SIZE   0x400U

#define OTA_META_MAGIC          0x4F54414DU   /* "MATO" */
#define OTA_META_VERSION        4U            /* v4: 无 app_valid */
#define OTA_IMAGE_MAGIC         0x4F544158U   /* "XATO" */

/* DID diagnostics markers (0x2114 during download reports 0x02 = backup) */
#define OTA_DL_TARGET_BACKUP    0x02U
#define OTA_SLOT_NONE           0xFEU         /* deprecated alias */
#define OTA_SLOT_A              0U            /* deprecated: App region */
#define OTA_SLOT_B              1U            /* deprecated alias */

#define OTA_STATE_IDLE               0x00U
#define OTA_STATE_DOWNLOADING        0x01U
#define OTA_STATE_VALIDATING         0x02U
#define OTA_STATE_PENDING_ACTIVATION 0x03U
#define OTA_STATE_TRIAL_BOOT         0x04U
#define OTA_STATE_CONFIRMED          0x05U
#define OTA_STATE_ROLLED_BACK        0x06U
#define OTA_STATE_FAILED             0x07U  /*!< OTA failed, previous APP intact */
#define OTA_STATE_MCU2_IN_PROGRESS   0x08U  /*!< MCU2 update in progress */
#define OTA_STATE_MCU2_NO_APP        0x09U  /*!< MCU2 has no valid APP */
#define OTA_STATE_MCU2_CONFIRMED     0x0AU  /*!< MCU2 update confirmed */

#define OTA_BOOT_REASON_POWER_ON   0x00U
#define OTA_BOOT_REASON_SW         0x01U
#define OTA_BOOT_REASON_WDG        0x02U
#define OTA_BOOT_REASON_OTA_ACT    0x03U
#define OTA_BOOT_REASON_COPY_FAIL  0x04U
#define OTA_BOOT_REASON_ROLLBACK   OTA_BOOT_REASON_COPY_FAIL /* deprecated alias */

/* exported types ---------------------------------------------------------- */

/**
 * @brief  XATO image header (256B, byte-frozen layout; @0x4C reserved
 *         placeholder — original version field removed, packed as 0x00)
 */
typedef struct
{
  uint32_t magic;               /* 0x4F544158 "XATO" */
  uint32_t image_length;       /* payload bytes after header */
  uint32_t crc32;              /* CRC32 of payload */
  uint8_t  signature[64];      /* ECDSA P-256 R||S */
  uint8_t  hdr_reserved_ver[16];
  uint32_t build_timestamp;
  uint8_t  reserved[160];
} ota_image_header_t;

/**
 * @brief  OTA metadata v4（28B，crc32 @0x18；与 boot_metadata.h 同构）
 */
typedef struct
{
  uint32_t magic;            /* @0x00 0x4F54414D "MATO" */
  uint32_t version;          /* @0x04 META_VERSION = 4 */
  uint8_t  backup_valid;     /* @0x08 APP 校验备份区后置 1；Boot 复验通过后清 0 */
  uint8_t  copy_fail_step;   /* @0x09 last backup-copy fail_step */
  uint8_t  last_boot_reason; /* @0x0A BOOT_REASON_* */
  uint8_t  reserved0;        /* @0x0B 对齐 */
  uint32_t backup_crc32;     /* @0x0C staging payload CRC */
  uint32_t copy_retry_count; /* @0x10 failed copy attempts (DID 0x2116) */
  uint8_t  ota_state;        /* @0x14 OTA_STATE_* */
  uint8_t  reserved[3];      /* @0x15 */
  uint32_t crc32;            /* @0x18 CRC32 of all above fields */
} ota_metadata_t;

/* exported functions ------------------------------------------------------ */

int8_t ota_metadata_read(ota_metadata_t *meta);
int8_t ota_metadata_save(const ota_metadata_t *meta);
uint32_t ota_crc32(const void *data, uint32_t length);

/* deprecated compatibility shims (single-App architecture) */
uint8_t ota_running_slot(void);      /* always 0: App region */
uint32_t ota_running_slot_base(void);/* always OTA_APP_BASE_ADDR */

#ifdef __cplusplus
}
#endif

#endif /* __OTA_TRIGGER_H */
