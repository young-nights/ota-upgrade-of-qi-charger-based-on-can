/**
  **************************************************************************
  * @file     main.c
  * @brief    QI Charger APP main program
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
#include "at32f422_426_clock.h"
#include "at32f422_426_conf.h"
#include "timer_drv.h"
#include "can_driver.h"
#include "can_protocol.h"
#include "lifecycle.h"
#include "ota_trigger.h"
#include "nvm_drv.h"
#include "qi_uart.h"
#include "qi_protocol.h"
#include "board_gpio.h"

/* private define ------------------------------------------------------------*/

int main(void)
{
  /* VTOR = App run address (single-App architecture, OTA-ARCH-0920) */
  SCB->VTOR = 0x08004100U;

  /* configure system clock to 180MHz */
  system_clock_config();
  nvic_priority_group_config(NVIC_PRIORITY_GROUP_4);

  /* initialize drivers while IRQ still masked from Boot jump / reset */
  timer_drv_init();
  board_gpio_init();
  nvm_drv_init();
  can_driver_init();
  can_protocol_init();
  /* qi_protocol_init 内含 qi_uart_init + register uart_rx_handler + rx_reset
   * + tx_seq=0；回调未注册时 0x2140 抓取/解析路径均收不到字节（1.1.13 修复）
   * （can_protocol_init 在前不动——不清 frame_callback，顺序安全） */
  qi_protocol_init();
  __enable_irq();

  /* SIT1145 powers up in Normal; enters Standby after 30 s UDS idle.
   * BOOTUP/OPERATIONAL after CAN online (first poll enter_normal). */
  lifecycle_init();

  /* main loop (trial-window logic removed: BOOT owns backup->App copy) */
  while (1)
  {
    timer_poll();
    can_protocol_poll();
    can_driver_poll();
    qi_uart_poll();
    lifecycle_poll();
    board_charge_poll();
  }
}

/**
  * @}
  */

/**
  * @}
  */
