/**
  **************************************************************************
  * @file     qi_uart.c
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

/* includes ------------------------------------------------------------------*/
#include "qi_uart.h"
#include "qi_uart_sniff.h"

/* private variables ---------------------------------------------------------*/

/** @brief  software RX ring buffer */
static volatile uint8_t rx_buf[QI_UART_RX_BUF_SIZE];
static volatile uint8_t rx_head = 0;   /*!< write index (ISR context) */
static volatile uint8_t rx_tail = 0;   /*!< read index  (main context) */
static volatile uint8_t rx_count = 0;  /*!< number of bytes in buffer */

/** @brief  RX callback function pointer */
static volatile qi_uart_rx_callback_t rx_callback = (qi_uart_rx_callback_t)0;

/* exported functions --------------------------------------------------------*/

/**
 * @brief  initialize USART2 for Qi chip communication
 * @note   configures PA2(TX) and PA3(RX) with AF mux,
 *         sets up USART2 at 9600 baud, 8N1, enables RX interrupt.
 * @param  none
 * @retval none
 */
void qi_uart_init(void)
{
  gpio_init_type gpio_init_struct;

  /* enable peripheral clocks */
  crm_periph_clock_enable(CRM_GPIOA_PERIPH_CLOCK, TRUE);
  crm_periph_clock_enable(CRM_USART2_PERIPH_CLOCK, TRUE);

  /* configure PA2 (USART2_TX) as alternate function push-pull */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = GPIO_PINS_2;
  gpio_init_struct.gpio_mode           = GPIO_MODE_MUX;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_NONE;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(GPIOA, &gpio_init_struct);
  gpio_pin_mux_config(GPIOA, GPIO_PINS_SOURCE2, GPIO_MUX_7);

  /* configure PA3 (USART2_RX) as input with pull-up */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = GPIO_PINS_3;
  gpio_init_struct.gpio_mode           = GPIO_MODE_MUX;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_UP;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(GPIOA, &gpio_init_struct);
  gpio_pin_mux_config(GPIOA, GPIO_PINS_SOURCE3, GPIO_MUX_7);

  /* configure USART2: 9600 baud, 8N1 */
  usart_init(USART2, QI_UART_BAUDRATE, USART_DATA_8BITS, USART_STOP_1_BIT);
  usart_parity_selection_config(USART2, USART_PARITY_NONE);

  /* enable transmitter and receiver */
  usart_transmitter_enable(USART2, TRUE);
  usart_receiver_enable(USART2, TRUE);

  /* enable RX interrupt (RXNE) */
  usart_interrupt_enable(USART2, USART_RDBF_INT, TRUE);

  /* configure NVIC for USART2 */
  nvic_irq_enable(USART2_IRQn, 3, 0);

  /* enable USART2 */
  usart_enable(USART2, TRUE);

  /* reset software FIFO */
  rx_head  = 0;
  rx_tail  = 0;
  rx_count = 0;
}

/**
 * @brief  send data over Qi UART (blocking)
 * @param  data: pointer to data buffer
 * @param  len: number of bytes to send
 * @retval none
 */
void qi_uart_send(const uint8_t *data, uint8_t len)
{
  uint8_t i;

  for (i = 0; i < len; i++)
  {
    /* wait for TX data register empty */
    while (usart_flag_get(USART2, USART_TDBE_FLAG) == RESET)
    {
    }
    usart_data_transmit(USART2, data[i]);
  }

  /* wait for transmission complete */
  while (usart_flag_get(USART2, USART_TDC_FLAG) == RESET)
  {
  }
}

/**
 * @brief  send a single byte over Qi UART (blocking)
 * @param  byte: byte to send
 * @retval none
 */
void qi_uart_send_byte(uint8_t byte)
{
  /* wait for TX data register empty */
  while (usart_flag_get(USART2, USART_TDBE_FLAG) == RESET)
  {
  }
  usart_data_transmit(USART2, byte);

  /* wait for transmission complete */
  while (usart_flag_get(USART2, USART_TDC_FLAG) == RESET)
  {
  }
}

/**
 * @brief  register a callback for received Qi UART data
 * @param  cb: callback function pointer, or NULL to unregister
 * @retval none
 */
void qi_uart_register_rx_callback(qi_uart_rx_callback_t cb)
{
  rx_callback = cb;
}

/**
 * @brief  check if there are bytes available in the RX buffer
 * @retval number of bytes available
 */
uint8_t qi_uart_rx_available(void)
{
  return rx_count;
}

/**
 * @brief  read a byte from the RX buffer
 * @note   entire read operation is performed with interrupts disabled
 *         to prevent race condition between main context read and ISR write.
 * @param  byte: pointer to store the read byte
 * @retval 0 on success, -1 if buffer is empty
 */
int8_t qi_uart_rx_read(uint8_t *byte)
{
  __disable_irq();
  if (rx_count == 0)
  {
    __enable_irq();
    return -1;
  }

  *byte = rx_buf[rx_tail];
  rx_tail = (rx_tail + 1) % QI_UART_RX_BUF_SIZE;
  rx_count--;
  __enable_irq();

  return 0;
}

/**
 * @brief  USART2 RX interrupt handler
 * @note   called from USART2_IRQHandler in interrupt context.
 *         reads received byte into software ring buffer.
 * @param  none
 * @retval none
 */
void qi_uart_rx_irq_handler(void)
{
  uint8_t received_byte;

  if (usart_flag_get(USART2, USART_RDBF_FLAG) != RESET)
  {
    /* read received byte */
    received_byte = (uint8_t)usart_data_receive(USART2);

    /* store in ring buffer if not full */
    if (rx_count < QI_UART_RX_BUF_SIZE)
    {
      rx_buf[rx_head] = received_byte;
      rx_head = (rx_head + 1) % QI_UART_RX_BUF_SIZE;
      rx_count++;
    }

    /* clear overrun flag if set */
    if (usart_flag_get(USART2, USART_ROERR_FLAG) != RESET)
    {
      usart_flag_clear(USART2, USART_ROERR_FLAG);
    }
  }
}

/**
 * @brief  poll RX buffer and invoke callback from main loop context
 * @note   should be called periodically from the main loop.
 *         processes one byte per call to keep latency bounded.
 * @param  none
 * @retval none
 */
void qi_uart_poll(void)
{
  uint8_t byte;

  if (rx_count > 0 && rx_callback != (qi_uart_rx_callback_t)0)
  {
    if (qi_uart_rx_read(&byte) == 0)
    {
      /* 旁路抓取：原始字节喂入 0x2140 抓取缓冲（零侵入，
       * 不改变下方 qi_protocol 回调解析路径与 USART2 ISR 行为） */
      qi_sniff_feed(byte);
      ((qi_uart_rx_callback_t)rx_callback)(&byte, 1);
    }
  }
}

/*
 * TODO: Qi protocol parsing layer
 * - implement Qi chip command/response protocol
 * - handle charging status, power control, FOD (foreign object detection)
 * - add timeout and retry logic for Qi chip communication
 */
