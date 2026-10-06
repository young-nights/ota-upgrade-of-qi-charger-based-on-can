/**
  **************************************************************************
  * @file     qi_uart.h
  * @brief    Qi wireless charging UART driver for communication with Qi chip
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
#ifndef __QI_UART_H
#define __QI_UART_H

#ifdef __cplusplus
extern "C" {
#endif

/* includes ------------------------------------------------------------------*/
#include "at32f422_426.h"

/* exported constants --------------------------------------------------------*/

/** @brief  Qi UART configuration */
/* Qi 芯片实测 19200，2026-10-06 用户确认，此前 9600 系文档口径错误 */
#define QI_UART_BAUDRATE        19200U         /*!< Qi UART baud (19200, user-confirmed 2026-10-06) */
#define QI_UART_RX_BUF_SIZE     64U            /*!< software RX ring buffer size */

/* exported types ------------------------------------------------------------*/

/**
 * @brief  Qi UART RX callback function type
 * @param  data: pointer to received byte
 * @param  len:  number of bytes available
 */
typedef void (*qi_uart_rx_callback_t)(uint8_t *data, uint8_t len);

/* exported functions -------------------------------------------------------*/

/**
 * @brief  initialize USART2 for Qi chip communication
 * @note   configures PA2(TX) and PA3(RX) with AF mux,
 *         sets up USART2 at 19200 baud, 8N1, enables RX interrupt.
 * @param  none
 * @retval none
 */
void qi_uart_init(void);

/**
 * @brief  send data over Qi UART (blocking)
 * @param  data: pointer to data buffer
 * @param  len: number of bytes to send
 * @retval none
 */
void qi_uart_send(const uint8_t *data, uint8_t len);

/**
 * @brief  send a single byte over Qi UART (blocking)
 * @param  byte: byte to send
 * @retval none
 */
void qi_uart_send_byte(uint8_t byte);

/**
 * @brief  register a callback for received Qi UART data
 * @param  cb: callback function pointer, or NULL to unregister
 * @retval none
 */
void qi_uart_register_rx_callback(qi_uart_rx_callback_t cb);

/**
 * @brief  check if there are bytes available in the RX buffer
 * @retval number of bytes available
 */
uint8_t qi_uart_rx_available(void);

/**
 * @brief  read a byte from the RX buffer
 * @param  byte: pointer to store the read byte
 * @retval 0 on success, -1 if buffer is empty
 */
int8_t qi_uart_rx_read(uint8_t *byte);

/**
 * @brief  USART2 RX interrupt handler
 * @note   called from USART2_IRQHandler in interrupt context
 * @param  none
 * @retval none
 */
void qi_uart_rx_irq_handler(void);

/**
 * @brief  poll RX buffer and invoke callback from main loop context
 * @note   should be called periodically from the main loop.
 *         processes one byte per call to keep latency bounded.
 * @param  none
 * @retval none
 */
void qi_uart_poll(void);

#ifdef __cplusplus
}
#endif

#endif /* __QI_UART_H */
