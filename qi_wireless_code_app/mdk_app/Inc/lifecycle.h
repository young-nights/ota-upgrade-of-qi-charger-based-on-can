/**
  **************************************************************************
  * @file     lifecycle.h
  * @brief    Lifecycle broadcast module for peripheral status reporting
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
#ifndef __LIFECYCLE_H
#define __LIFECYCLE_H

#ifdef __cplusplus
extern "C" {
#endif

/* includes ------------------------------------------------------------------*/
#include "at32f422_426.h"

/* ========================================================================== */
/*  Lifecycle state definitions (0x18FF260D byte0，CCU 词汇表 2026-10-28)     */
/*  仅允许以下 4 值；[1..7] 全部清零                                         */
/* ========================================================================== */

#define LIFECYCLE_BOOTUP       0x01U   /*!< 上电/复位，开始启动（APP announce 发；Boot 侧已回退） */
#define LIFECYCLE_OPERATIONAL  0x02U   /*!< 启动完成，可正常工作 */
#define LIFECYCLE_WAKEUP       0x03U   /*!< 保留词汇位：固件不发送（不区分冷启动/唤醒启动） */
#define LIFECYCLE_SLEEP        0x04U   /*!< 即将进入休眠 */

/* ========================================================================== */
/*  Exported functions                                                       */
/* ========================================================================== */

/**
 * @brief  initialize lifecycle module
 * @note   must be called after can_driver_init().
 *         BOOTUP 由 Bootloader 在上电 100ms 内发送，APP 不发 BOOTUP。
 * @param  none
 * @retval none
 */
void lifecycle_init(void);

/**
 * @brief  set lifecycle state and post immediate broadcast
 * @note   call this whenever the device state changes.
 *         OPERATIONAL enables 1s periodic broadcast.
 * @param  state: new lifecycle state (LIFECYCLE_BOOTUP .. LIFECYCLE_SLEEP)
 * @retval none
 */
void lifecycle_set_state(uint8_t state);

/**
 * @brief  post one state frame (send now, or queue while MF UDS busy)
 * @note   MF 期间不丢弃，入 pending FIFO，lifecycle_flush 补发。
 *         队尾同值去重；LIFECYCLE_SLEEP 不入队（走 can_lp_go_sleep 专径）。
 * @param  state: lifecycle state value
 * @retval none
 */
void lifecycle_post(uint8_t state);

/**
 * @brief  flush pending state frames (call every main-loop turn)
 * @note   仅在 !uds_mf_busy() 时按 FIFO 顺序补发；成功才出队。
 * @param  none
 * @retval none
 */
void lifecycle_flush(void);

/**
 * @brief  drop all pending state frames (only before SLEEP entry)
 * @param  none
 * @retval none
 */
void lifecycle_pending_drop(void);

/**
 * @brief  poll lifecycle for periodic broadcast
 * @note   must be called from the main loop.
 *         sends periodic broadcast every 1000ms in OPERATIONAL state.
 *         周期帧不得刷新空闲休眠计时。
 * @param  none
 * @retval none
 */
void lifecycle_poll(void);

/**
 * @brief  get current lifecycle state
 * @retval current lifecycle state value
 */
uint8_t lifecycle_get_state(void);

#ifdef __cplusplus
}
#endif

#endif /* __LIFECYCLE_H */
