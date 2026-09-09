/**
  **************************************************************************
  * @file     can_driver.h
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

/* define to prevent recursive inclusion -------------------------------------*/
#ifndef __CAN_DRIVER_H
#define __CAN_DRIVER_H

#ifdef __cplusplus
extern "C" {
#endif

/* includes ------------------------------------------------------------------*/
#include "at32f422_426.h"

/* exported constants --------------------------------------------------------*/

/**
 * @brief  CAN ID definitions for Qi wireless charger communication
 */
#define CAN_ID_UDS_REQUEST              0x18DA0D03U  /*!< UDS request  (CCU -> Qi) */
#define CAN_ID_UDS_RESPONSE             0x18DA030DU  /*!< UDS response (Qi -> CCU) */
#define CAN_ID_FUNCTIONAL_REQUEST       0x18DB33F1U  /*!< UDS functional: PF=DB PS=33 SA any */
#define CAN_ID_LIFECYCLE_BROADCAST      0x18FF260DU  /*!< Qi module status (100ms) */
#define CAN_ID_CCU_CONTROL              0x18FF270DU  /*!< CCU control command */

/**
 * @brief  CAN driver configuration constants
 */
#define CAN_DRIVER_RX_FIFO_SIZE         16U          /*!< software RX FIFO depth */
#define CAN_DRIVER_MAX_DATA_LEN         8U           /*!< max data bytes per frame */

/* exported types ------------------------------------------------------------*/

/**
 * @brief  CAN received frame structure (software FIFO element)
 */
typedef struct
{
  uint32_t id;                            /*!< 29-bit extended identifier */
  uint8_t  data[CAN_DRIVER_MAX_DATA_LEN]; /*!< frame data (up to 8 bytes) */
  uint8_t  len;                           /*!< actual data length (0~8) */
} can_rx_frame_t;

/**
 * @brief  CAN RX callback function type
 * @param  id:   29-bit extended identifier of received frame
 * @param  data: pointer to received data buffer
 * @param  len:  data length (0~8)
 */
typedef void (*can_rx_callback_t)(uint32_t id, uint8_t *data, uint8_t len);

/**
 * @brief  CAN bus-off recovery callback function type
 * @note   invoked from can_driver_poll() after bus-off recovery is detected.
 */
typedef void (*can_busoff_recovery_callback_t)(void);

/* exported functions -------------------------------------------------------*/

/**
 * @brief  initialize CAN1 peripheral in extended frame mode at 250kbps
 * @note   configures PA11(CAN_RX) and PA12(CAN_TX) with AF mux,
 *         sets up acceptance filter to accept all extended frames,
 *         enables RX and error interrupts.
 * @param  none
 * @retval none
 */
void can_driver_init(void);

/**
 * @brief  transmit a CAN extended frame
 * @param  id:   29-bit extended identifier
 * @param  data: pointer to transmit data buffer
 * @param  len:  data length (0~8)
 * @retval 0 on success, -1 on failure (bus busy or invalid parameter)
 */
int8_t can_driver_send(uint32_t id, uint8_t *data, uint8_t len);

/**
 * @brief  pop one frame from the software RX FIFO (no callback)
 * @retval 0 if a frame was copied, -1 if FIFO empty
 */
int8_t can_driver_recv(uint32_t *id, uint8_t *data, uint8_t *len);

/**
 * @brief  wait until a TX mailbox has finished (or timeout)
 * @param  timeout_ms: maximum wait
 * @retval 0 on success, -1 on timeout
 */
int8_t can_driver_wait_tx_idle(uint32_t timeout_ms);

/**
 * @brief  register a callback for received CAN frames
 * @note   the callback is invoked from can_driver_poll() in main loop context
 * @param  cb: callback function pointer, or NULL to unregister
 * @retval none
 */
void can_driver_register_rx_callback(can_rx_callback_t cb);

/**
 * @brief  process received CAN frames from software FIFO
 * @note   must be called periodically from main loop.
 *         invokes registered callback for each pending frame.
 * @param  none
 * @retval none
 */
void can_driver_poll(void);

/**
 * @brief  CAN1 RX interrupt handler (called from CAN1_RX_IRQHandler)
 * @note   reads frame from hardware RX buffer into software FIFO,
 *         clears interrupt flag.
 * @param  none
 * @retval none
 */
void can_driver_rx_irq_handler(void);

/**
 * @brief  CAN1 error interrupt handler (called from CAN1_ERR_IRQHandler)
 * @note   clears error flags and performs error recovery if bus-off.
 * @param  none
 * @retval none
 */
void can_driver_err_irq_handler(void);

/**
 * @brief  register a callback for bus-off recovery event
 * @note   the callback is invoked from can_driver_poll() when bus-off
 *         recovery is detected. keeps ISR context minimal.
 * @param  cb: callback function pointer, or NULL to unregister
 * @retval none
 */
void can_driver_register_busoff_recovery_callback(can_busoff_recovery_callback_t cb);

/**
 * @brief  take CAN1 offline (software reset, IRQs off)
 * @note   call after SIT1145 Standby so RXD-low wake does not bus-off the MCU.
 */
void can_driver_offline(void);

/**
 * @brief  bring CAN1 online (re-apply timing/filters, leave reset, IRQs on)
 * @note   call after SIT1145 has entered Normal and CTS is set.
 */
void can_driver_online(void);

/**
 * @brief  PA11/PA12 back to CAN AF4 (call before SIT1145 Normal + can_driver_online)
 */
void can_driver_pins_active(void);

/**
 * @brief  PA12 GPIO out low, PA11 GPIO in pull-up (SIT1145 Standby WUP listen)
 */
void can_driver_pins_standby(void);

#ifdef __cplusplus
}
#endif

#endif /* __CAN_DRIVER_H */
