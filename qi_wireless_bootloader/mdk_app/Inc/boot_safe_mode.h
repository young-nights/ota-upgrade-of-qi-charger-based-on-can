/**
  **************************************************************************
  * @file     boot_safe_mode.h
  * @brief    Safe mode (UDS OTA download) interface
  **************************************************************************
  */

#ifndef __BOOT_SAFE_MODE_H
#define __BOOT_SAFE_MODE_H

#include <stdint.h>

/**
 * @brief  enter safe mode: persist failure context to metadata reserved
 *         fields, init CAN, answer safe-mode probe frames (frame format
 *         documented in boot_safe_mode.c, keep in sync with
 *         python_tools/zcanpro_ext_ota_auto.py)
 * @note   entered when no bootable slot remains. Does not return.
 * @param  cause: 0x02 = no valid App image after copy attempt,
 *                0x03 = backup copy verification failed (detail in
 *                metadata copy_fail_step)
 * @retval none (does not return)
 */
void enter_safe_mode(uint8_t cause);

/* ===== Boot diagnostic marker frames (observation only) =====
 * Boot decision chain markers on CAN ID 0x18FF480D (extended, DLC=8),
 * frame = [marker 0xA1..0xA4][data...] padded with 0xCC. Single-App
 * architecture (OTA-ARCH-0920) payloads: see marker prototypes below.
 * Bounded polling TX (wait until TRANSMITTED, give up on timeout); safe-mode legacy
 * frames (62 21 13 FE fail_step / ABT heartbeat) untouched. */
#define BOOT_DIAG_CAN_ID  0x18FF480DU
/* Single-App (OTA-ARCH-0920) marker payloads:
 *   M1 0xA1: [meta_src 0=primary/1=backup-copy/2=defaults]
 *            [magic_ok][ver_ok][crc_ok]
 *   M2 0xA2: [copy result 0=start-or-none/1=committed/0xFF=failed]
 *            [detail: fail_step or 0xFF=not-pending]
 *            （字节序统一 b1=copy result, b2=detail：boot_safe_mode.c
 *            实现与 zcanpro_boot_diag_capture.py 解码同序，wire 即文档序；
 *            OTA-ARCH-0920-D5，勿再单独改其一）
 *   M3 0xA3: [pass][fail_step 0xFF=verifying][target 0=Backup/1=App]
 *   M4 0xA4: [jump target addr LE 4B] (App entry 0x08003100) */

/** @brief 诊断用 CAN 提前初始化（main.c step2，timer 之后/metadata 之前） */
void boot_diag_can_init(void);
/** @brief 生命周期 BOOTUP 帧（0x18FF260D [01 00×7]）——上电 100ms 内必发，
 *    can_init 之后立即调用（CCU 词汇表 2026-10-28：byte0 四值、[1..7] 清零） */
void boot_lifecycle_bootup(void);
/** @brief M1：metadata 读完记录后（meta=ota_metadata_t*） */
void boot_diag_m1(const void *meta);
/** @brief M2：backup copy decision point (result + detail) */
void boot_diag_m2(int8_t ret, uint8_t info);
/** @brief M3：image verify point (pass/fail + region target) */
void boot_diag_m3(uint8_t pass, uint8_t fail_step, uint8_t target);
/** @brief M4：boot_jump_to_app 跳转前 */
void boot_diag_m4(uint32_t app_addr);

/** @brief boot_metadata_init 记录：0=主区生效 1=备区恢复 2=defaults；0xFF=未记录 */
extern uint8_t g_diag_meta_src;

#endif /* __BOOT_SAFE_MODE_H */
