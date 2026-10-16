/**
 **************************************************************************
 * @file     boot_metadata.h
 * @brief    OTA metadata management for bootloader (Boot + App architecture)
 **************************************************************************
 *
 * Single-App OTA architecture (OTA-ARCH-0920, A/B slots removed):
 *   Boot     0x08000000..0x08002FFF  12KB  (uvprojx IROM 0x08000000,0x3000)
 *   App      0x08003000..0x0800FFFF  52KB  XATO header @0x08003000,
 *                                          code entry  @0x08003100
 *   Backup   0x08010000..0x0801CFFF  52KB  pending-upgrade firmware image
 *   Metadata 0x0801D000 / 0x0801D800 primary / backup copies
 *   DeviceInfo 0x0801E000  NVM 0x0801F000..0x0801FFFF
 *
 * Upgrade flow: host streams firmware into Backup via APP UDS
 * (0x31/0x34/0x36/0x37) -> APP verifies image -> APP sets backup_valid
 * flag in metadata -> reset -> BOOT erases App region, copies Backup ->
 * App, re-verifies, clears flag, jumps to App.
 *
 * Metadata struct v4 (28 bytes, crc32 at offset 0x18, META_VERSION=4),
 * defined identically in boot/app. v3 及更早拒绝 → 默认重建。
 * v4 删除无门禁意义的 app_valid；backup_valid 仍在 @0x08。
 */

#ifndef __BOOT_METADATA_H
#define __BOOT_METADATA_H

#ifdef __cplusplus
extern "C" {
#endif

#include "at32f422_426.h"

/* exported constants ------------------------------------------------------ */

#define BOOT_BASE_ADDR          0x08000000U
#define BOOT_SIZE               0x3000U       /* 12KB bootloader */
#define APP_BASE_ADDR           0x08003000U   /* App image region (incl. header) */
#define APP_SIZE                0xD000U       /* 52KB */
#define BACKUP_BASE_ADDR        0x08010000U   /* pending-firmware staging region */
#define BACKUP_SIZE             0xD000U       /* 52KB */
#define IMAGE_HEADER_SIZE       256U
#define APP_ENTRY_ADDR          (APP_BASE_ADDR + IMAGE_HEADER_SIZE) /* 0x08003100 */
#define META_PRIMARY_ADDR       0x0801D000U
#define META_BACKUP_ADDR        0x0801D800U
#define META_PAGE_SIZE          0x400U
#define DEVICE_INFO_ADDR        0x0801E000U
#define DEVICE_INFO_SIZE        0x1000U
#define FLASH_SECTOR_SIZE       0x400U
#define SRAM_BASE_ADDR          0x20000000U
#define SRAM_SIZE               0x5000U

#define META_MAGIC              0x4F54414DU   /* "MATO" */
#define META_VERSION            4U            /* v4: 无 app_valid */

/* boot reason codes */
#define BOOT_REASON_POWER_ON    0x00U
#define BOOT_REASON_SW          0x01U
#define BOOT_REASON_WDG         0x02U
#define BOOT_REASON_OTA_ACT     0x03U         /* backup copy in progress */
#define BOOT_REASON_COPY_FAIL   0x04U         /* backup copy verification failed */

/* ota_state codes */
#define OTA_STATE_IDLE          0x00U
#define OTA_STATE_DOWNLOADING   0x01U


/* exported types ---------------------------------------------------------- */

/**
 * @brief  OTA metadata v4（28B，crc32 @0x18；与 ota_trigger.h 同构）
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

int8_t boot_metadata_init(ota_metadata_t *meta);
int8_t boot_metadata_save(ota_metadata_t *meta);
uint32_t boot_crc32(const void *data, uint32_t length);
uint32_t boot_crc32_continue(uint32_t crc, const void *data, uint32_t length);

#ifdef __cplusplus
}
#endif

#endif /* __BOOT_METADATA_H */
