/**
  **************************************************************************
  * @file     can_driver.c
  * @brief    CAN 2.0B extended frame driver for AT32F426
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
#include "can_driver.h"
#include "timer_drv.h"
#include "sit1145.h"
#include <string.h>

/* private define ------------------------------------------------------------*/

/**
 * @brief  Classic CAN 2.0B bit timing (CAN FD unused)
 * @note   AT32F426 CAST CAN-CTRL:
 *         baudrate = can_clk / (div * (bts1 + bts2))
 *         sample   = bts1 / (bts1 + bts2)
 *         BTS1 already includes SYNC_SEG (do not add +1).
 *         CAN kernel = PLL 180 MHz, div = 10 → 18 MHz tq clock
 *         54 + 18 = 72 Tq → 18 MHz / 72 = 250 kbps, SP = 54/72 = 75%
 */
#define CAN_BITTIME_DIV                 10U
#define CAN_BITTIME_SJW                 4U
#define CAN_BITTIME_BTS1                54U
#define CAN_BITTIME_BTS2                18U

/* private variables ---------------------------------------------------------*/

/** @brief  software RX FIFO ring buffer */
static volatile can_rx_frame_t rx_fifo[CAN_DRIVER_RX_FIFO_SIZE];
static volatile uint8_t rx_fifo_head = 0;   /*!< write index (ISR context) */
static volatile uint8_t rx_fifo_tail = 0;   /*!< read index  (main context) */
static volatile uint8_t rx_fifo_count = 0;  /*!< number of pending frames */

/** @brief  RX callback function pointer */
static volatile can_rx_callback_t rx_callback = (can_rx_callback_t)0;

/* private functions ---------------------------------------------------------*/

/**
 * @brief  check if software RX FIFO is full
 * @retval 1 = full, 0 = not full
 */
static uint8_t rx_fifo_is_full(void)
{
  return (rx_fifo_count >= CAN_DRIVER_RX_FIFO_SIZE) ? 1U : 0U;
}

/* exported functions --------------------------------------------------------*/

/**
 * @brief  initialize CAN1 peripheral in extended frame mode at 250kbps
 * @note   configures PA11(CAN_RX) and PA12(CAN_TX) with AF mux,
 *         brings the CAST CAN-CTRL out of software reset after config,
 *         filter accepts all classic extended data frames (bench test).
 * @param  none
 * @retval none
 */
void can_driver_init(void)
{
  gpio_init_type gpio_init_struct;
  can_bittime_type can_bittime_struct;
  can_filter_config_type can_filter_struct;

  /* enable peripheral clocks */
  crm_periph_clock_enable(CRM_GPIOA_PERIPH_CLOCK, TRUE);
  crm_periph_clock_enable(CRM_CAN1_PERIPH_CLOCK, TRUE);

  /* CAN kernel clock defaults to HEXT (8 MHz). Bit timing below assumes 180 MHz.
   * 8e6 / 10 / 72 ≈ 11.1 kbps — that is what a bitrate probe shows if this is omitted.
   * Datasheet: CAN clock must come from PLL sourced by HEXT. */
  crm_can_clock_select(CRM_CAN1, CRM_CAN_CLOCK_SOURCE_PLL);

  /* configure PA11 (CAN_RX) as input with pull-up */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = GPIO_PINS_11;
  gpio_init_struct.gpio_mode           = GPIO_MODE_MUX;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_UP;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(GPIOA, &gpio_init_struct);
  gpio_pin_mux_config(GPIOA, GPIO_PINS_SOURCE11, GPIO_MUX_4);

  /* configure PA12 (CAN_TX) as alternate function push-pull */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = GPIO_PINS_12;
  gpio_init_struct.gpio_mode           = GPIO_MODE_MUX;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_NONE;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(GPIOA, &gpio_init_struct);
  gpio_pin_mux_config(GPIOA, GPIO_PINS_SOURCE12, GPIO_MUX_4);

  /* transceiver must be in Normal Mode before the CAN controller leaves reset */
  if (sit1145_init() == 0U)
  {
    (void)sit1145_init();
  }

  /* CRM reset leaves CTRLSTAT.RESET=1; config bits are only writable then */
  can_reset(CAN1);
  can_software_reset(CAN1, TRUE);

  /* set CAN to normal communication mode */
  can_mode_set(CAN1, CAN_MODE_COMMUNICATE);

  /* classic CAN only: 250 kbps, 75% SP. FD fields stay at BSP defaults */
  can_bittime_default_para_init(&can_bittime_struct);
  can_bittime_struct.bittime_div  = CAN_BITTIME_DIV;
  can_bittime_struct.ac_rsaw_size = CAN_BITTIME_SJW;
  can_bittime_struct.ac_bts1_size = CAN_BITTIME_BTS1;
  can_bittime_struct.ac_bts2_size = CAN_BITTIME_BTS2;
  can_bittime_set(CAN1, &can_bittime_struct);

  /* Filter 0: physical UDS request 0x18DA0D03 (exact 29-bit ID).
   * CAST ACF: mask bit 1 = don't care, 0 = must match code.
   * data_length must stay 0xF; 0 only accepts DLC=0 and drops UDS SF. */
  can_filter_default_para_init(&can_filter_struct);
  can_filter_struct.code_para.id         = CAN_ID_UDS_REQUEST;
  can_filter_struct.code_para.id_type    = CAN_ID_EXTENDED;
  can_filter_struct.code_para.frame_type = CAN_FRAME_DATA;
  can_filter_struct.mask_para.id         = 0x1FFFFFFFU;
  can_filter_struct.mask_para.id_type    = TRUE;
  can_filter_struct.mask_para.frame_type = TRUE;
  can_filter_struct.mask_para.data_length = 0x0FU;
  can_filter_struct.mask_para.recv_frame = TRUE;
  can_filter_set(CAN1, CAN_FILTER_NUM_0, &can_filter_struct);
  can_filter_enable(CAN1, CAN_FILTER_NUM_0, TRUE);

  /* Filter 1: functional UDS 0x18DB33xx (SA ignored) */
  can_filter_default_para_init(&can_filter_struct);
  can_filter_struct.code_para.id         = 0x18DB3300U;
  can_filter_struct.code_para.id_type    = CAN_ID_EXTENDED;
  can_filter_struct.code_para.frame_type = CAN_FRAME_DATA;
  can_filter_struct.mask_para.id         = 0x1FFFFF00U;
  can_filter_struct.mask_para.id_type    = TRUE;
  can_filter_struct.mask_para.frame_type = TRUE;
  can_filter_struct.mask_para.data_length = 0x0FU;
  can_filter_struct.mask_para.recv_frame = TRUE;
  can_filter_set(CAN1, CAN_FILTER_NUM_1, &can_filter_struct);
  can_filter_enable(CAN1, CAN_FILTER_NUM_1, TRUE);

  /* leave software reset so the controller actually participates on the bus */
  can_software_reset(CAN1, FALSE);

  /* enable RX interrupt and error interrupt after leaving reset */
  can_interrupt_enable(CAN1, CAN_RIE_INT, TRUE);
  can_interrupt_enable(CAN1, CAN_EIE_INT, TRUE);

  /* configure NVIC for CAN1 RX */
  nvic_irq_enable(CAN1_RX_IRQn, 1, 0);

  /* configure NVIC for CAN1 Error */
  nvic_irq_enable(CAN1_ERR_IRQn, 2, 0);

  /* reset software FIFO */
  rx_fifo_head  = 0;
  rx_fifo_tail  = 0;
  rx_fifo_count = 0;
  rx_callback   = (can_rx_callback_t)0;
}

/**
 * @brief  transmit a CAN extended frame
 * @param  id:   29-bit extended identifier
 * @param  data: pointer to transmit data buffer
 * @param  len:  data length (0~8)
 * @retval 0 on success, -1 on failure (bus busy or invalid parameter)
 */
int8_t can_driver_send(uint32_t id, uint8_t *data, uint8_t len)
{
  can_txbuf_type tx_buf;
  can_txbuf_select_type txbuf_sel;
  can_stb_status_type stb_status;
  can_transmit_status_type tx_status;
  uint8_t i;

  /* validate parameters */
  if ((data == (uint8_t *)0) || (len > CAN_DRIVER_MAX_DATA_LEN))
  {
    return -1;
  }

  /* CAST CAN-CTRL has SUPPORT_CAN_FD. Uninitialized fd_format/brs on the
   * stack sets FDF=1 and the host traces MCU replies as CAN FD. */
  memset(&tx_buf, 0, sizeof(tx_buf));
  tx_buf.id             = id;
  tx_buf.id_type        = CAN_ID_EXTENDED;
  tx_buf.frame_type     = CAN_FRAME_DATA;
  tx_buf.fd_format      = CAN_FORMAT_CLASSIC;
  tx_buf.fd_rate_switch = CAN_BRS_OFF;
  tx_buf.handle         = 0;
  tx_buf.tx_timestamp   = FALSE;

  /* set data length code */
  switch (len)
  {
    case 0: tx_buf.data_length = CAN_DLC_BYTES_0; break;
    case 1: tx_buf.data_length = CAN_DLC_BYTES_1; break;
    case 2: tx_buf.data_length = CAN_DLC_BYTES_2; break;
    case 3: tx_buf.data_length = CAN_DLC_BYTES_3; break;
    case 4: tx_buf.data_length = CAN_DLC_BYTES_4; break;
    case 5: tx_buf.data_length = CAN_DLC_BYTES_5; break;
    case 6: tx_buf.data_length = CAN_DLC_BYTES_6; break;
    case 7: tx_buf.data_length = CAN_DLC_BYTES_7; break;
    default: tx_buf.data_length = CAN_DLC_BYTES_8; break;
  }

  /* copy data */
  for (i = 0; i < len; i++)
  {
    tx_buf.data[i] = data[i];
  }

  /* try primary transmit buffer (PTB) first for higher priority.
   * TRANSMITTED(3) also means PTB is free for a new frame. */
  can_transmit_status_get(CAN1, &tx_status);
  if ((tx_status.current_tstat == CAN_TSTAT_IDLE) ||
      (tx_status.current_tstat == CAN_TSTAT_TRANSMITTED))
  {
    txbuf_sel = CAN_TXBUF_PTB;
  }
  else
  {
    /* check secondary transmit buffer (STB) */
    stb_status = can_stb_status_get(CAN1);
    if (stb_status == CAN_STB_STATUS_FULL)
    {
      return -1;  /* both buffers full */
    }
    txbuf_sel = CAN_TXBUF_STB;
  }

  /* write to transmit buffer */
  if (can_txbuf_write(CAN1, txbuf_sel, &tx_buf) != SUCCESS)
  {
    return -1;
  }

  /* trigger transmission */
  if (txbuf_sel == CAN_TXBUF_PTB)
  {
    can_txbuf_transmit(CAN1, CAN_TRANSMIT_PTB);
  }
  else
  {
    can_txbuf_transmit(CAN1, CAN_TRANSMIT_STB_ONE);
  }

  return 0;
}

/**
 * @brief  register a callback for received CAN frames
 * @note   the callback is invoked from can_driver_poll() in main loop context
 * @param  cb: callback function pointer, or NULL to unregister
 * @retval none
 */
void can_driver_register_rx_callback(can_rx_callback_t cb)
{
  rx_callback = cb;
}

int8_t can_driver_recv(uint32_t *id, uint8_t *data, uint8_t *len)
{
  uint8_t i;
  uint8_t n;

  if ((id == (uint32_t *)0) || (data == (uint8_t *)0) || (len == (uint8_t *)0))
  {
    return -1;
  }

  __disable_irq();
  if (rx_fifo_count == 0U)
  {
    __enable_irq();
    return -1;
  }

  *id  = rx_fifo[rx_fifo_tail].id;
  n    = rx_fifo[rx_fifo_tail].len;
  *len = n;
  for (i = 0; i < n; i++)
  {
    data[i] = rx_fifo[rx_fifo_tail].data[i];
  }
  rx_fifo_tail = (rx_fifo_tail + 1) % CAN_DRIVER_RX_FIFO_SIZE;
  rx_fifo_count--;
  __enable_irq();
  return 0;
}

int8_t can_driver_wait_tx_idle(uint32_t timeout_ms)
{
  uint32_t start = timer_get_tick();
  can_transmit_status_type tx_status;

  do
  {
    can_transmit_status_get(CAN1, &tx_status);
    if ((tx_status.current_tstat == CAN_TSTAT_IDLE) ||
        (tx_status.current_tstat == CAN_TSTAT_TRANSMITTED))
    {
      return 0;
    }
  } while ((timer_get_tick() - start) < timeout_ms);

  return -1;
}

/**
 * @brief  process received CAN frames from software FIFO
 * @note   must be called periodically from main loop.
 *         invokes registered callback for each pending frame.
 * @param  none
 * @retval none
 */
void can_driver_poll(void)
{
  can_rx_frame_t frame;

  while (rx_fifo_count > 0)
  {
    /* disable IRQ to protect the entire FIFO read+advance sequence from ISR race */
    __disable_irq();

    /* copy frame from FIFO */
    frame.id  = rx_fifo[rx_fifo_tail].id;
    frame.len = rx_fifo[rx_fifo_tail].len;
    {
      uint8_t i;
      for (i = 0; i < frame.len; i++)
      {
        frame.data[i] = rx_fifo[rx_fifo_tail].data[i];
      }
    }

    /* advance tail pointer and decrement count */
    rx_fifo_tail = (rx_fifo_tail + 1) % CAN_DRIVER_RX_FIFO_SIZE;
    rx_fifo_count--;

    __enable_irq();

    /* invoke callback */
    if (rx_callback != (can_rx_callback_t)0)
    {
      rx_callback(frame.id, frame.data, frame.len);
    }
  }
}

/**
 * @brief  CAN1 RX interrupt handler (called from CAN1_RX_IRQHandler)
 * @note   reads frame from hardware RX buffer into software FIFO,
 *         clears interrupt flag.
 * @param  none
 * @retval none
 */
void can_driver_rx_irq_handler(void)
{
  can_rxbuf_type rx_buf;
  uint8_t i;

  /* check RX interrupt flag */
  if (can_flag_get(CAN1, CAN_RIF_FLAG) != RESET)
  {
    /* read frame from hardware RX buffer */
    if (can_rxbuf_read(CAN1, &rx_buf) == SUCCESS)
    {
      /* store in software FIFO if not full */
      if (!rx_fifo_is_full())
      {
        rx_fifo[rx_fifo_head].id  = rx_buf.id;
        rx_fifo[rx_fifo_head].len = (uint8_t)(rx_buf.data_length & 0x0F);
        if (rx_fifo[rx_fifo_head].len > CAN_DRIVER_MAX_DATA_LEN)
        {
          rx_fifo[rx_fifo_head].len = CAN_DRIVER_MAX_DATA_LEN;
        }
        for (i = 0; i < rx_fifo[rx_fifo_head].len; i++)
        {
          rx_fifo[rx_fifo_head].data[i] = rx_buf.data[i];
        }
        rx_fifo_head = (rx_fifo_head + 1) % CAN_DRIVER_RX_FIFO_SIZE;
        rx_fifo_count++;
      }
    }

    /* release RX buffer */
    can_rxbuf_release(CAN1);

    /* clear RX interrupt flag */
    can_flag_clear(CAN1, CAN_RIF_FLAG);
  }

  /* handle RX overflow */
  if (can_flag_get(CAN1, CAN_ROIF_FLAG) != RESET)
  {
    can_flag_clear(CAN1, CAN_ROIF_FLAG);
  }
}

/**
 * @brief  CAN1 error interrupt handler (called from CAN1_ERR_IRQHandler)
 * @note   clears error flags and performs error recovery if bus-off.
 * @param  none
 * @retval none
 */
void can_driver_err_irq_handler(void)
{
  /* check error warning flag */
  if (can_flag_get(CAN1, CAN_EIF_FLAG) != RESET)
  {
    /* check for bus-off condition */
    if (can_busoff_get(CAN1) != RESET)
    {
      /* recover from bus-off by requesting recovery */
      can_busoff_reset(CAN1);
    }

    /* clear error interrupt flag */
    can_flag_clear(CAN1, CAN_EIF_FLAG);
  }

  /* clear bus error flag if set */
  if (can_flag_get(CAN1, CAN_BEIF_FLAG) != RESET)
  {
    can_flag_clear(CAN1, CAN_BEIF_FLAG);
  }

  /* clear arbitration lost flag if set */
  if (can_flag_get(CAN1, CAN_ALIF_FLAG) != RESET)
  {
    can_flag_clear(CAN1, CAN_ALIF_FLAG);
  }

  /* clear error passive flag if set */
  if (can_flag_get(CAN1, CAN_EPIF_FLAG) != RESET)
  {
    can_flag_clear(CAN1, CAN_EPIF_FLAG);
  }
}
