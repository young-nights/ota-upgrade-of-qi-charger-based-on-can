/**
 **************************************************************************
 * @file     boot_trial.c
 * @brief    Backup->App copy engine + boot decision (OTA-ARCH-0920)
 **************************************************************************
 *
 * Single-App OTA: APP streams new firmware into the Backup region and
 * sets meta.backup_valid; this file (Boot side) performs the physical
 * copy Backup -> App. An APP cannot erase the flash region it executes
 * from, so the copy must happen here, before jumping.
 *
 * Power-loss idempotence: backup_valid is cleared only AFTER the copied
 * image re-verifies in the App region. Interruptions at any point leave
 * the flag set; the next power-on re-runs the whole sequence.
 */

#include "boot_trial.h"
#include "boot_verify.h"
#include "boot_jump.h"
#include "boot_safe_mode.h"
#include "at32f422_426_conf.h"
#include <string.h>

ota_metadata_t g_meta;

uint8_t detect_boot_reason(void)
{
  uint8_t reason = BOOT_REASON_POWER_ON;
  flag_status wdt  = crm_flag_get(CRM_WDT_RESET_FLAG);
  flag_status wwdt = crm_flag_get(CRM_WWDT_RESET_FLAG);
  flag_status sw   = crm_flag_get(CRM_SW_RESET_FLAG);
  flag_status por  = crm_flag_get(CRM_POR_RESET_FLAG);

  /* snapshot first: crm_flag_clear(RSTFC) clears every reset flag */
  if ((wdt != RESET) || (wwdt != RESET))
  {
    reason = BOOT_REASON_WDG;
  }
  else if (sw != RESET)
  {
    reason = BOOT_REASON_SW;
  }
  else if (por != RESET)
  {
    reason = BOOT_REASON_POWER_ON;
  }

  crm_flag_clear(CRM_ALL_RESET_FLAG);
  return reason;
}



/**
 * @brief  是否有待激活的 Backup→App 搬运
 * @param  meta  已由 boot_metadata_init() 加载的 RAM 副本
 * @retval 1  backup_valid != 0，main step3 应走 boot_copy_backup()
 *         0  无待搬运，跳过拷贝
 * @note   只读旗子，不读 Backup 区、不算 CRC。
 *         旗子由 APP commit_backup() 置位，仅搬运成功才清。
 */
int8_t boot_backup_pending(const ota_metadata_t *meta)
{
  return (meta->backup_valid != 0U) ? 1 : 0;
}



 /**
 * @brief  擦除整个 APP 区 Flash
 *
 * 从 APP_BASE_ADDR 起，按扇区擦除 APP_SIZE 字节。
 * 全程关中断并持有 Flash 解锁，避免擦除过程被打断。
 *
 * @retval  0   全部扇区擦除成功
 * @retval -1  某一扇区擦除失败（已重新上锁并开中断）
 *
 * @note  static 仅本文件可见。失败时已擦部分不会回滚。
 * @warning 会清掉 APP 区全部内容，调用前确认地址/长度宏正确。
 */
static int8_t copy_erase_app_region(void)
{
  uint32_t addr;
  flash_status_type status;

  /* 擦扇区期间禁止被 ISR 打断 */
  __disable_irq();    
  /* 允许写 Flash 控制寄存器 */
  flash_unlock();

  for (addr = APP_BASE_ADDR; addr < (APP_BASE_ADDR + APP_SIZE); addr += FLASH_SECTOR_SIZE)
  {
    status = flash_sector_erase(addr);
    if (status != FLASH_OPERATE_DONE)
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
 * @brief  word-program [src, src+len) at dst, then readback verify
 * @note   IRQ disabled per write burst (single-bank flash contention,
 *         same rationale as meta_write_to_flash in boot_metadata.c)
 */
static int8_t copy_program_region(uint32_t dst, const uint8_t *src,
                                  uint32_t len)
{
  uint32_t i;
  uint32_t words;
  const uint32_t *wsrc = (const uint32_t *)src;

  words = (len + 3U) / 4U;
  __disable_irq();
  flash_unlock();
  for (i = 0U; i < words; i++)
  {
    if (flash_word_program(dst + (i * 4U), wsrc[i]) != FLASH_OPERATE_DONE)
    {
      flash_lock();
      __enable_irq();
      return -1;
    }
  }
  for (i = 0U; i < words; i++)
  {
    if (*(volatile uint32_t *)(dst + (i * 4U)) != wsrc[i])
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
 * @brief  将 Backup 区待激活镜像搬运到 App 区并提交
 * @param  meta  RAM 中的 metadata（调用前 backup_valid 应为 1）
 * @retval  0  commit 完成（旗子已清、已 save）
 *         -1  中途失败（旗子仍为 1，已 save 失败上下文）
 * @note   向量窗强制按 App 区解释：Backup 里的镜像必须是链接到
 *         0x08003000 的图，不能是「在 Backup 地址上能跑」的图。
 *         M3 target：0=Backup，1=App；fail_step=0xFF 表示「即将开始验」。
 */
int8_t boot_copy_backup(ota_metadata_t *meta)
{
  const ota_image_view_t *hdr;
  uint32_t total;

  hdr = (const ota_image_view_t *)BACKUP_BASE_ADDR;

  /* 1. verify Backup image; vectors must target the App window */
  boot_diag_m3(0U, 0xFFU, 0U); /* pre-verify marker: target=Backup region */
  if (boot_verify_image(BACKUP_BASE_ADDR, BACKUP_SIZE,APP_BASE_ADDR, APP_SIZE) != 0)
  {
    meta->copy_fail_step = g_verify_fail_step;
    meta->copy_retry_count++;
    meta->last_boot_reason = BOOT_REASON_COPY_FAIL;
    (void)boot_metadata_save(meta);
    boot_diag_m3(0U, g_verify_fail_step, 0U);
    return -1;
  }
  boot_diag_m3(1U, g_verify_fail_step, 0U);

  /* staging consistency: metadata record must match image CRC */
  if (meta->backup_crc32 != hdr->crc32)
  {
    meta->copy_fail_step = 0xFFU; /* record mismatch */
    meta->copy_retry_count++;
    meta->last_boot_reason = BOOT_REASON_COPY_FAIL;
    (void)boot_metadata_save(meta);
    return -1;
  }

  /* 2. erase App region */
  if (copy_erase_app_region() != 0)
  {
    meta->copy_fail_step = 0xFEU; /* erase fail */
    meta->copy_retry_count++;
    meta->last_boot_reason = BOOT_REASON_COPY_FAIL;
    (void)boot_metadata_save(meta);
    return -1;
  }

  /* 3. copy header + payload from Backup to App */
  total = IMAGE_HEADER_SIZE + hdr->image_length;
  if (copy_program_region(APP_BASE_ADDR, (const uint8_t *)BACKUP_BASE_ADDR,
                          total) != 0)
  {
    meta->copy_fail_step = 0xFDU; /* program fail */
    meta->copy_retry_count++;
    meta->last_boot_reason = BOOT_REASON_COPY_FAIL;
    (void)boot_metadata_save(meta);
    return -1;
  }

  /* 4. re-verify image in App region */
  boot_diag_m3(0U, 0xFFU, 1U); /* pre-verify marker: target=App region */
  if (boot_verify_image(APP_BASE_ADDR, APP_SIZE,
                        APP_BASE_ADDR, APP_SIZE) != 0)
  {
    meta->copy_fail_step = g_verify_fail_step;
    meta->copy_retry_count++;
    meta->last_boot_reason = BOOT_REASON_COPY_FAIL;
    (void)boot_metadata_save(meta);
    boot_diag_m3(0U, g_verify_fail_step, 1U);
    return -1;
  }

  /* 5. commit: only now clear the pending flag (power-loss safe) */
  meta->backup_valid = 0U;
  meta->ota_state    = OTA_STATE_IDLE;
  meta->last_boot_reason = BOOT_REASON_OTA_ACT;
  meta->copy_fail_step = 0U;
  (void)boot_metadata_save(meta);
  boot_diag_m3(1U, g_verify_fail_step, 1U);
  return 0;
}

int8_t boot_app_image_ok(void)
{
  if (boot_verify_image(APP_BASE_ADDR, APP_SIZE,
                        APP_BASE_ADDR, APP_SIZE) == 0)
  {
    return 0;
  }
  return -1;
}
