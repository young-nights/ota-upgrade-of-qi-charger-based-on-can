/**
 **************************************************************************
 * @file     boot_metadata.c
 * @brief    OTA metadata management for bootloader (Boot + App architecture)
 **************************************************************************
 *
 * Dual-copy metadata at 0x0801D000 / 0x0801D800. Single-App format
 * (META_VERSION=4): v3 及更早拒绝 -> 默认重建
 * rebuild (Q2 slimming).
 * All writes funnel through meta_write_to_flash (IRQ-off single-bank
 * erase+program+readback) — power-safe, backup copy written first.
 */

#include "boot_metadata.h"
#include "boot_safe_mode.h"
#include "at32f422_426_conf.h"
#include <string.h>

#define META_CRC32_OFFSET    (sizeof(ota_metadata_t) - sizeof(uint32_t))

static int8_t meta_flash_erase_page(uint32_t addr)
{
  flash_status_type status;

  status = flash_sector_erase(addr);
  if (status != FLASH_OPERATE_DONE)
  {
    return -1;
  }
  return 0;
}

static int8_t meta_flash_write_word(uint32_t addr, uint32_t data)
{
  flash_status_type status;

  status = flash_word_program(addr, data);
  if (status != FLASH_OPERATE_DONE)
  {
    return -1;
  }
  return 0;
}

/**
 * @brief  write ota_metadata_t to one flash sector (IRQ-off: single-bank
 *         erase/program while fetching from same bank wedges the core,
 *         incident 4ec5858 — every metadata save funnels through here)
 */
static int8_t meta_write_to_flash(uint32_t addr, const ota_metadata_t *meta)
{
  const uint32_t *src;
  uint32_t words;
  uint32_t i;

  __disable_irq();

  flash_unlock();

  if (meta_flash_erase_page(addr) != 0)
  {
    flash_lock();
    __enable_irq();
    return -1;
  }

  src   = (const uint32_t *)meta;
  words = sizeof(ota_metadata_t) / sizeof(uint32_t);

  for (i = 0; i < words; i++)
  {
    if (meta_flash_write_word(addr + (i * 4U), src[i]) != 0)
    {
      flash_lock();
      __enable_irq();
      return -1;
    }
  }

  for (i = 0; i < words; i++)
  {
    if (*(volatile uint32_t *)(addr + (i * 4U)) != src[i])
    {
      flash_lock();
      __enable_irq();
      return -1;
    }
  }

  flash_lock();

  __enable_irq();
  return 0;
}


/**
 * @brief  校验一份 Flash/RAM 中的 ota_metadata_t 是否可采信
 * @param  meta  指向主区或备区（或 RAM 副本）
 * @retval  0  magic + version + CRC 均通过
 *         -1  任一检查失败（空片、旧版、写损）
 * @note   不算 backup_valid；那是待搬运旗，不是结构完整性。
 *         CRC 覆盖范围是结构体去掉末尾 crc32 字段
 *         （META_CRC32_OFFSET = sizeof(ota_metadata_t) - 4）。
 */
static int8_t meta_validate(const ota_metadata_t *meta)
{
  uint32_t computed_crc;
  uint32_t stored_crc;

  /* 扇区身份：必须是 "MATO"，空 Flash 或乱数据在此出局 */
  if (meta->magic != META_MAGIC)
  {
    return -1;
  }

  /* 布局版本：只接受 META_VERSION==4，v3 及更早强制重建 */
  if (meta->version != META_VERSION)
  {
    return -1;
  }

  /* 完整性：先取出尾部存值，再对前面字段重算，避免把 CRC 自己算进去 */
  stored_crc   = meta->crc32;
  computed_crc = boot_crc32((const void *)meta, META_CRC32_OFFSET);
  if (computed_crc != stored_crc)
  {
    return -1;
  }
  return 0;
}



static void meta_fill_defaults(ota_metadata_t *meta)
{
  memset((void *)meta, 0, sizeof(ota_metadata_t));

  meta->magic             = META_MAGIC;
  meta->version           = META_VERSION;
  meta->backup_valid      = 0U;
  meta->copy_fail_step    = 0U;
  meta->last_boot_reason  = BOOT_REASON_POWER_ON;
  meta->backup_crc32      = 0U;
  meta->copy_retry_count  = 0U;
  meta->ota_state         = OTA_STATE_IDLE;
  meta->reserved[0]       = 0U;
  meta->reserved[1]       = 0U;
  meta->reserved[2]       = 0U;
}

uint32_t boot_crc32_continue(uint32_t crc, const void *data, uint32_t length)
{
  const uint8_t *p = (const uint8_t *)data;
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
  return crc;
}

uint32_t boot_crc32(const void *data, uint32_t length)
{
  return boot_crc32_continue(0xFFFFFFFFU, data, length) ^ 0xFFFFFFFFU;
}


/**
 * @brief  加载 OTA metadata 到 RAM（主区优先，双备份）
 * @param  meta  调用方提供的 RAM 缓冲（通常是 g_meta）
 * @retval  0  主区或备区校验通过，已拷入 meta
 *         -1  两边都无效，已写入默认值（空片 / 旧版 / CRC 坏）
 * @note   校验：magic "MATO" + META_VERSION==4 + CRC32。
 *         v2 及更早直接失败，走默认重建。
 *         每条路径结束都会设 g_diag_meta_src 并发 M1。
 *         备区恢复 / 默认重建才会 boot_metadata_save（先备后主）。
 */
int8_t boot_metadata_init(ota_metadata_t *meta)
{
  const ota_metadata_t *primary;
  const ota_metadata_t *backup;

  /* 路径 1：主区 0x0801D000 完整 → 只读入 RAM，不写 Flash */
  primary = (const ota_metadata_t *)META_PRIMARY_ADDR;
  if (meta_validate(primary) == 0)
  {
    memcpy((void *)meta, (const void *)primary, sizeof(ota_metadata_t));
    g_diag_meta_src = 0U;   /* M1.b2 = 主区生效 */
    boot_diag_m1(meta);
    return 0;
  }

  /* 路径 2：主区坏、备区 0x0801D800 完整 → 用备区修主区 */
  backup = (const ota_metadata_t *)META_BACKUP_ADDR;
  if (meta_validate(backup) == 0)
  {
    memcpy((void *)meta, (const void *)backup, sizeof(ota_metadata_t));
    boot_metadata_save(meta);
    g_diag_meta_src = 1U;   /* M1.b2 = 备区生效 */
    boot_diag_m1(meta);
    return 0;
  }

  /* 路径 3：双份都废 → 填默认（app/backup 均无效），落盘后继续 boot */
  meta_fill_defaults(meta);
  meta->crc32 = boot_crc32((const void *)meta, META_CRC32_OFFSET);
  boot_metadata_save(meta);
  g_diag_meta_src = 2U;   /* M1.b2 = 默认值生效 */
  boot_diag_m1(meta);

  return -1;
}




int8_t boot_metadata_save(ota_metadata_t *meta)
{
  meta->crc32 = boot_crc32((const void *)meta, META_CRC32_OFFSET);

  /* backup copy first so a torn primary write stays recoverable */
  if (meta_write_to_flash(META_BACKUP_ADDR, meta) != 0)
  {
    return -1;
  }
  if (meta_write_to_flash(META_PRIMARY_ADDR, meta) != 0)
  {
    return -1;
  }
  return 0;
}
