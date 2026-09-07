/**
  **************************************************************************
  * @file     boot_jump.c
  * @brief    Application jump logic for bootloader
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
#include "boot_jump.h"
#include "boot_metadata.h"
#include "core_cm4.h"
#include "at32f422_426_conf.h"
#include "sit1145.h"

/**
 * @brief  Jump-to-APP method selection
 *         0 = inline assembly (msr msp + bx) — safe across all ARM compilers
 *         1 = CMSIS __set_MSP + function pointer — matches official IAP example
 */
#ifndef BOOT_JUMP_METHOD
#define BOOT_JUMP_METHOD  0
#endif

#if (BOOT_JUMP_METHOD == 1U)
typedef void (*app_entry_t)(void);
#endif

int8_t boot_jump_vectors_ok(uint32_t app_addr)
{
  uint32_t app_msp;
  uint32_t reset_fn;

  /* Cortex-M4 VTOR: bits [6:0] must be 0 (128-byte aligned). */
  if ((app_addr & 0x7FU) != 0U)
  {
    return -1;
  }

  app_msp  = *(volatile uint32_t *)(app_addr);
  reset_fn = (*(volatile uint32_t *)(app_addr + 4U)) & 0xFFFFFFFEU;

  /* Initial SP may equal SRAM_BASE+SIZE (one past last byte). AAPCS: 8-byte aligned. */
  if ((app_msp < SRAM_BASE_ADDR) ||
      (app_msp > (SRAM_BASE_ADDR + SRAM_SIZE)) ||
      ((app_msp & 7U) != 0U))
  {
    return -1;
  }
  if ((reset_fn < (APP_A_BASE_ADDR + IMAGE_HEADER_SIZE)) ||
      (reset_fn >= META_PRIMARY_ADDR))
  {
    return -1;
  }
  return 0;
}

void boot_jump_to_app(uint32_t app_addr)
{
  uint32_t msp;
  uint32_t reset;

  if (boot_jump_vectors_ok(app_addr) != 0)
  {
    return;
  }

  /* snapshot from Flash before MSP switches to the APP stack */
  msp   = *(volatile uint32_t *)(app_addr);
  reset = (*(volatile uint32_t *)(app_addr + 4U)) | 1U;

  __disable_irq();

  /* disable CAN1 */
  can_reset(CAN1);
  crm_periph_clock_enable(CRM_CAN1_PERIPH_CLOCK, FALSE);

  /* disable SPI1 */
  spi_enable(SPI1, FALSE);
  crm_periph_clock_enable(CRM_SPI1_PERIPH_CLOCK, FALSE);

  SysTick->CTRL = 0U;
  SysTick->LOAD = 0U;
  SysTick->VAL  = 0U;
  SCB->ICSR = SCB_ICSR_PENDSTCLR_Msk;

  NVIC->ICER[0] = 0xFFFFFFFFU;
  NVIC->ICER[1] = 0xFFFFFFFFU;
  NVIC->ICPR[0] = 0xFFFFFFFFU;
  NVIC->ICPR[1] = 0xFFFFFFFFU;

  /* privileged thread, MSP, no FPCA (Boot may have used FPU) */
  __set_CONTROL(0U);
  __DSB();
  __ISB();

  SCB->VTOR = app_addr;
  __DSB();
  __ISB();

#if (BOOT_JUMP_METHOD == 0U)
  /* Method 0: inline assembly — do not touch C locals after MSR MSP */
  __ASM volatile(
    "msr msp, %0 \n"
    "bx  %1      \n"
    :
    : "r" (msp), "r" (reset)
    : "memory"
  );
#else
  /* Method 1: CMSIS __set_MSP + function pointer (official IAP style) */
  (void)reset;  /* unused: Reset_Handler address loaded via function pointer */
  __set_MSP(msp);
  ((app_entry_t)(*(volatile uint32_t *)(app_addr + 4U)))();
#endif

  while (1)
  {
    __NOP();
  }
}
