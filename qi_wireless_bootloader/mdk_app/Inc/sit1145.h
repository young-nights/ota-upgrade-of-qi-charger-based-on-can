/**
  **************************************************************************
  * @file     sit1145.h
  * @brief    SIT1145 CAN FD transceiver driver for AT32F426
  **************************************************************************
  */

#ifndef __SIT1145_H
#define __SIT1145_H

#ifdef __cplusplus
extern "C" {
#endif

/* includes ------------------------------------------------------------------*/
#include "at32f422_426.h"

/* exported constants --------------------------------------------------------*/

/* SIT1145 SPI CS pin definition (PA4, active low) */
#define SIT1145_CS_GPIO_PORT        GPIOA
#define SIT1145_CS_GPIO_PIN         GPIO_PINS_4

/* SIT1145 floating pin PB3 (set to output low) */
#define SIT1145_FLOAT_GPIO_PORT     GPIOB
#define SIT1145_FLOAT_GPIO_PIN      GPIO_PINS_3

/* SPI read/write protocol bits */
#define SIT1145_WRITE               0x00
#define SIT1145_READ                0x01

/* SIT1145 register addresses */
#define SIT1145_REG_MODE_CONTROL          0x01
#define SIT1145_REG_CAN_CONTROL           0x20
#define SIT1145_REG_TRANSCEIVER_STATUS    0x22
#define SIT1145_REG_DATA_RATE             0x26
#define SIT1145_REG_IDENTIFICATION        0x7E

/* Identification register values (datasheet table 28) */
#define SIT1145_ID_AQ               0x70  /* SIT1145AQ */
#define SIT1145_ID_AQ_FD            0x74  /* SIT1145AQ/FD */

/* Transceiver status register bits (0x22) */
#define SIT1145_TRAN_STA_CTS        (1U << 7)  /* 1 = in Normal, ready to transmit */

/* Mode control register values */
#define SIT1145_MC_SLEEP_MODE       0x01
#define SIT1145_MC_STANDBY_MODE     0x04
#define SIT1145_MC_NORMAL_MODE      0x07
#define SIT1145_MC_MODE_MASK        0x07

/* CAN control register bit definitions */
#define SIT1145_CAN_CTRL_CFDC_POS   6U   /* CAN FD tolerance */
#define SIT1145_CAN_CTRL_PNCOK_POS  5U   /* partial networking config */
#define SIT1145_CAN_CTRL_CPNC_POS   4U   /* selective wakeup */
#define SIT1145_CAN_CTRL_CMC_POS    0U   /* CAN transceiver mode */
#define SIT1145_CAN_CTRL_CMC_MASK   0x03

/* CAN control configuration values */
#define SIT1145_CAN_FD_TOLERANCE_EN (1U << SIT1145_CAN_CTRL_CFDC_POS)
#define SIT1145_CAN_NETW_INVALID    (0U << SIT1145_CAN_CTRL_PNCOK_POS)
#define SIT1145_SEL_WAKEUP_DIS      (0U << SIT1145_CAN_CTRL_CPNC_POS)
#define SIT1145_CAN_MODE_NORMAL     0x01  /* normal mode with VCC undervoltage recovery */

/* Data rate register values */
#define SIT1145_DRATE_50K           0x00
#define SIT1145_DRATE_100K          0x01
#define SIT1145_DRATE_125K          0x02
#define SIT1145_DRATE_250K          0x03
#define SIT1145_DRATE_500K          0x05
#define SIT1145_DRATE_1000K         0x07

/* Default CAN configuration for this project: 250kbps */
#define SIT1145_DEFAULT_DRATE       SIT1145_DRATE_250K

/* exported functions -------------------------------------------------------*/

/**
 * @brief  initialize SIT1145 CAN transceiver
 * @note   SPI Mode 1 (CPOL=0, CPHA=1), then CAN Control, Data Rate,
 *         Normal Mode, CTS poll, identification check.
 *         Called from can_driver_init() before the CAN controller leaves reset.
 * @retval 1 on success, 0 on failure
 */
uint8_t sit1145_init(void);

/**
 * @brief  write SIT1145 register via SPI
 * @param  addr: register address
 * @param  data: value to write
 * @retval none
 */
void sit1145_write_reg(uint8_t addr, uint8_t data);

/**
 * @brief  read SIT1145 register via SPI
 * @param  addr: register address
 * @retval register value
 */
uint8_t sit1145_read_reg(uint8_t addr);

/**
 * @brief  set SIT1145 to Normal Mode
 * @note   Bootloader 只允许 Normal，不进入 Standby/Sleep。
 * @retval 1 on success, 0 on failure
 */
uint8_t sit1145_normal_mode_set(void);

/**
 * @brief  get current SIT1145 operating mode
 * @retval Mode control register value & 0x07
 */
uint8_t sit1145_get_mode(void);

#ifdef __cplusplus
}
#endif

#endif /* __SIT1145_H */
