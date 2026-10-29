/**
  **************************************************************************
  * @file     lifecycle.c
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

/* includes ------------------------------------------------------------------*/
#include "lifecycle.h"
#include "app_config.h"
#include "can_protocol.h"
#include "can_driver.h"
#include "timer_drv.h"
#include <string.h>

/* ========================================================================== */
/*  Private constants                                                        */
/* ========================================================================== */

/* 周期广播开关/间隔见 app_config.h（LIFECYCLE_PERIODIC_*） */

/* pending FIFO 深度：合法事件序列最大 BOOTUP→WAKEUP→OPERATIONAL→SLEEP=4 */
#define LIFECYCLE_PENDING_MAX   4U

/* ========================================================================== */
/*  Private variables                                                        */
/* ========================================================================== */

/** @brief  current lifecycle state */
static uint8_t  g_lifecycle_state = LIFECYCLE_BOOTUP;

/** @brief  timestamp of last broadcast (for periodic sending) */
static uint32_t g_last_broadcast_tick = 0;

/** @brief  pending state frames deferred during MF UDS transfer */
static uint8_t  g_pending[LIFECYCLE_PENDING_MAX];
static uint8_t  g_pending_count = 0U;

/* ========================================================================== */
/*  Private functions                                                        */
/* ========================================================================== */

/**
 * @brief  send a lifecycle broadcast frame
 * @note   载荷 [0]=state，[1..7]=0x00（CCU 词汇表，2026-10-28）。
 *         周期/事件帧都不得刷新空闲休眠计时（不调 can_lp_mark_uds）。
 * @param  state: lifecycle state value to send in byte 0
 * @retval 1=已发出，0=被门禁拦下（未入队，调用方决定是否 post）
 */
static uint8_t lifecycle_send(uint8_t state)
{
  uint8_t data[8];

  if (can_protocol_lifecycle_tx_ready() == 0U)
  {
    return 0U;
  }

  memset(data, 0, sizeof(data));
  data[0] = state;

  can_driver_send(CAN_ID_LIFECYCLE_BROADCAST, data, 8);
  return 1U;
}

/**
 * @brief  check if current state supports periodic broadcast
 * @param  state: lifecycle state
 * @retval 1 = periodic broadcast enabled, 0 = disabled
 */
static uint8_t lifecycle_is_periodic(uint8_t state)
{
#if LIFECYCLE_PERIODIC_ENABLE
  return (state == LIFECYCLE_OPERATIONAL) ? 1U : 0U;
#else
  (void)state;
  return 0U;
#endif
}

/* ========================================================================== */
/*  Bus-off recovery callback                                                */
/* ========================================================================== */

/**
 * @brief  bus-off recovery callback
 * @note   called from can_driver_poll() after bus-off recovery.
 *         重发当前状态（不再伪造 BOOTUP）；1s 限频防 TXD 极性错误时刷屏。
 * @retval none
 */
static void lifecycle_on_busoff_recovery(void)
{
  uint32_t now = timer_get_tick();

  if ((now - g_last_broadcast_tick) < 1000U)
  {
    return;
  }

  lifecycle_post(g_lifecycle_state);
  g_last_broadcast_tick = now;
}

/* ========================================================================== */
/*  Exported functions                                                       */
/* ========================================================================== */

/**
 * @brief  initialize lifecycle module
 * @note   BOOTUP 由 Bootloader 在上电 100ms 内发送，APP 不发 BOOTUP。
 * @param  none
 * @retval none
 */
void lifecycle_init(void)
{
  g_lifecycle_state = LIFECYCLE_BOOTUP;
  g_last_broadcast_tick = timer_get_tick();
  g_pending_count = 0U;

  can_driver_register_busoff_recovery_callback(lifecycle_on_busoff_recovery);
}

/**
 * @brief  post one state frame (send now, or queue while MF UDS busy)
 * @param  state: lifecycle state value
 * @retval none
 */
void lifecycle_post(uint8_t state)
{
  if ((state < LIFECYCLE_BOOTUP) || (state > LIFECYCLE_SLEEP))
  {
    return;
  }

  /* SLEEP 不入队：由 can_lp_go_sleep 专径保证送达（等 MF + handle 确认 + 重试） */
  if (state == LIFECYCLE_SLEEP)
  {
    (void)lifecycle_send(state);
    return;
  }

  if (lifecycle_send(state) != 0U)
  {
    return;
  }

  /* MF 期间：入 pending FIFO，队尾同值去重（1s 周期帧最多挂 1 条） */
  if ((g_pending_count > 0U) &&
      (g_pending[g_pending_count - 1U] == state))
  {
    return;
  }
  if (g_pending_count < LIFECYCLE_PENDING_MAX)
  {
    g_pending[g_pending_count++] = state;
  }
}

/**
 * @brief  flush pending state frames (call every main-loop turn)
 * @param  none
 * @retval none
 */
void lifecycle_flush(void)
{
  while (g_pending_count > 0U)
  {
    if (lifecycle_send(g_pending[0]) == 0U)
    {
      return;  /* 仍忙，留队下轮再试 */
    }
    {
      uint8_t i;
      g_pending_count--;
      for (i = 0U; i < g_pending_count; i++)
      {
        g_pending[i] = g_pending[i + 1U];
      }
    }
  }
}

/**
 * @brief  drop all pending state frames (only before SLEEP entry)
 * @note   SLEEP 取代一切未发状态；最坏丢 1 条 1s 周期 OPERATIONAL。
 * @param  none
 * @retval none
 */
void lifecycle_pending_drop(void)
{
  g_pending_count = 0U;
}

/**
 * @brief  set lifecycle state and post immediate broadcast
 * @param  state: new lifecycle state
 * @retval none
 */
void lifecycle_set_state(uint8_t state)
{
  if ((state < LIFECYCLE_BOOTUP) || (state > LIFECYCLE_SLEEP))
  {
    return;  /* invalid state, ignore */
  }

  g_lifecycle_state = state;
  lifecycle_post(state);
  g_last_broadcast_tick = timer_get_tick();
}

/**
 * @brief  poll lifecycle for periodic broadcast
 * @note   must be called from the main loop.
 *         仅 OPERATIONAL 态每 1000ms 周期广播；SLEEP 后停止。
 *         周期帧不得刷新空闲休眠计时。
 * @param  none
 * @retval none
 */
void lifecycle_poll(void)
{
  uint32_t now;

  lifecycle_flush();

  if (can_protocol_is_bus_awake() == 0U)
  {
    return;
  }

  if (!lifecycle_is_periodic(g_lifecycle_state))
  {
    return;
  }

  now = timer_get_tick();
  if ((now - g_last_broadcast_tick) >= LIFECYCLE_PERIODIC_INTERVAL_MS)
  {
    g_last_broadcast_tick = now;
    lifecycle_post(g_lifecycle_state);
  }
}

/**
 * @brief  get current lifecycle state
 * @retval current lifecycle state value
 */
uint8_t lifecycle_get_state(void)
{
  return g_lifecycle_state;
}
