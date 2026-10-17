/**
  **************************************************************************
  * @file     isotp.h
  * @brief    ISO 15765-2 (ISO-TP) transport layer for CAN UDS communication
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
#ifndef __ISOTP_H
#define __ISOTP_H

#ifdef __cplusplus
extern "C" {
#endif

/* includes ------------------------------------------------------------------*/
#include "at32f422_426.h"

/* exported constants --------------------------------------------------------*/

/** @brief  ISO-TP PCI frame type identifiers (upper nibble of first byte) */
#define ISOTP_PCI_TYPE_SF               0x00U        /*!< Single Frame */
#define ISOTP_PCI_TYPE_FF               0x10U        /*!< First Frame */
#define ISOTP_PCI_TYPE_CF               0x20U        /*!< Consecutive Frame */
#define ISOTP_PCI_TYPE_FC               0x30U        /*!< Flow Control */

/** @brief  ISO-TP PCI type mask (upper nibble) */
#define ISOTP_PCI_TYPE_MASK             0xF0U

/** @brief  ISO-TP maximum payload buffer size (12-bit FF length => 4095 max) */
#define ISOTP_MAX_PAYLOAD               4095U

/** @brief  CAN frame data field size (CAN 2.0B) */
#define ISOTP_CAN_FRAME_SIZE            8U

/** @brief  ISO-TP Flow Control status values */
#define ISOTP_FC_STATUS_CTS             0x00U        /*!< Continue To Send */
#define ISOTP_FC_STATUS_WAIT            0x01U        /*!< Wait */
#define ISOTP_FC_STATUS_OVERFLOW        0x02U        /*!< Overflow */

/** @brief  ISO-TP default Block Size (number of CF frames before next FC) */
#define ISOTP_FC_DEFAULT_BS             0x00U        /*!< 0 = send all remaining */

/** @brief  ISO-TP default Separation Time (ms between CF frames) */
#define ISOTP_FC_DEFAULT_STMIN          0x01U        /*!< 1 ms separation time (per spec) */

/** @brief  N_Bs timeout waiting for Flow Control after First Frame (ms) */
#define ISOTP_N_BS_TIMEOUT_MS           1000U

/** @brief  N_Cr timeout waiting for next Consecutive Frame (ms) */
#define ISOTP_N_CR_TIMEOUT_MS           1000U

/** @brief  N_As timeout for a single CAN frame transmit (ms) */
#define ISOTP_N_AS_TIMEOUT_MS           1000U

/* exported types ------------------------------------------------------------*/

/**
 * @brief  ISO-TP receiver state machine states
 */
typedef enum
{
  ISOTP_RX_STATE_IDLE = 0,              /*!< waiting for a new message */
  ISOTP_RX_STATE_RX_IN_PROGRESS,       /*!< receiving consecutive frames */
} isotp_rx_state_t;

/**
 * @brief  ISO-TP receiver context
 * @note   holds the reassembly buffer and state for incoming multi-frame
 *         ISO-TP messages.
 */
typedef struct
{
  isotp_rx_state_t state;               /*!< current receiver state */
  uint8_t  buf[ISOTP_MAX_PAYLOAD];      /*!< reassembly buffer */
  uint16_t total_len;                   /*!< total expected payload length */
  uint16_t received_len;                /*!< bytes received so far */
  uint8_t  expected_sn;                 /*!< expected next sequence number */
  uint32_t last_cf_ms;                  /*!< tick of last CF / FF (N_Cr) */
} isotp_rx_ctx_t;

/**
 * @brief  ISO-TP completion callback type
 * @note   invoked when a complete ISO-TP message has been reassembled.
 *         all data bytes are the UDS payload (PCI already stripped).
 * @param  data: pointer to the complete UDS payload
 * @param  len:  payload length in bytes
 */
typedef void (*isotp_complete_cb_t)(uint8_t *data, uint16_t len);

/* exported functions -------------------------------------------------------*/

/**
 * @brief  initialize the ISO-TP receiver
 * @note   resets the receiver state machine and registers the completion
 *         callback.  must be called before processing any CAN frames.
 * @param  cb: callback invoked when a complete message is reassembled
 * @retval none
 */
void isotp_init(isotp_complete_cb_t cb);

/**
 * @brief  process an incoming CAN frame through ISO-TP
 * @note   this function handles SF/FF/CF/FC frame types automatically.
 *         for single-frame messages, the callback fires immediately.
 *         for multi-frame messages, data is buffered until complete.
 *         should be called from the CAN RX callback for each received frame.
 * @param  data: pointer to CAN frame data (up to 8 bytes)
 * @param  len:  CAN frame data length
 * @retval none
 */
void isotp_rx_process(uint8_t *data, uint8_t len);

/**
 * @brief  poll ISO-TP receiver timeouts (N_Cr)
 * @note   call from the main loop.
 */
void isotp_poll(void);

/**
 * @brief  send a UDS payload via ISO-TP segmentation
 * @note   segments the payload into SF or FF+CF frames as needed and
 *         transmits them using the provided CAN send function.
 *         for multi-frame, sends FF then all CFs (no FC wait on this MCU).
 * @param  can_id:  CAN identifier to use for transmission
 * @param  payload: pointer to UDS payload data
 * @param  len:     payload length in bytes (1..4095)
 * @retval 0 on success, -1 on failure
 */
int8_t isotp_tx_send(uint32_t can_id, uint8_t *payload, uint16_t len);

#ifdef __cplusplus
}
#endif

#endif /* __ISOTP_H */
