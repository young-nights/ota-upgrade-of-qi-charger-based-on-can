/**
  **************************************************************************
  * @file     nvm_drv.h
  * @brief    Non-volatile memory driver using internal Flash for config storage
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
#ifndef __NVM_DRV_H
#define __NVM_DRV_H

#ifdef __cplusplus
extern "C" {
#endif

/* includes ------------------------------------------------------------------*/
#include "at32f422_426.h"

/* exported constants --------------------------------------------------------*/

/**
 * @brief  NVM configuration area layout
 * @note   AT32F426 with 128KB Flash uses 2KB sectors.
 *         Metadata lives at 0x0801D000 (primary) and 0x0801D800 (backup).
 *         NVM config uses 0x0801F000 ~ 0x0801FFFF (last 4KB = 1 sector).
 */
#define NVM_CONFIG_BASE_ADDR            0x0801F000U  /*!< config area start address */
#define NVM_CONFIG_END_ADDR             0x0801FFFFU  /*!< config area end address */
#define NVM_CONFIG_SIZE                 0x1000U      /*!< config area size: 4KB */
#define NVM_SECTOR_SIZE                 0x800U       /*!< sector size: 2KB for AT32F426 */
#define NVM_VALIDITY_MAGIC              0x4E564D31U  /*!< magic: "NVM1" in ASCII */
#define NVM_VALIDITY_OFFSET             0x0000U      /*!< offset of validity magic word */

/* exported types ------------------------------------------------------------*/

/**
 * @brief  NVM operation status
 */
typedef enum
{
  NVM_STATUS_OK           = 0x00,  /*!< operation successful */
  NVM_STATUS_ERROR        = 0x01,  /*!< general error */
  NVM_STATUS_INVALID_ADDR = 0x02,  /*!< invalid address or offset */
  NVM_STATUS_NOT_INIT     = 0x03,  /*!< NVM not initialized or invalid */
  NVM_STATUS_FLASH_ERROR  = 0x04   /*!< flash operation error */
} nvm_status_t;

/* exported functions -------------------------------------------------------*/

/**
 * @brief  initialize NVM driver and check config area validity
 * @note   checks for validity magic word at base address.
 *         if magic is not found, the config area is considered uninitialized.
 * @retval NVM_STATUS_OK if config area is valid, NVM_STATUS_NOT_INIT otherwise
 */
nvm_status_t nvm_drv_init(void);

/**
 * @brief  read data from NVM config area
 * @param  offset: byte offset from config area base (0 ~ NVM_CONFIG_SIZE-1)
 * @param  buf:    pointer to destination buffer
 * @param  len:    number of bytes to read
 * @retval NVM_STATUS_OK on success, error code otherwise
 */
nvm_status_t nvm_drv_read(uint16_t offset, uint8_t *buf, uint16_t len);

/**
 * @brief  write data to NVM config area (erase-then-write)
 * @note   this function erases the affected sector(s) before writing.
 *         data not covered by this write within the same sector will be
 *         preserved by read-modify-write (erase + rewrite).
 * @param  offset: byte offset from config area base (0 ~ NVM_CONFIG_SIZE-1)
 * @param  buf:    pointer to source data buffer
 * @param  len:    number of bytes to write
 * @retval NVM_STATUS_OK on success, error code otherwise
 */
nvm_status_t nvm_drv_write(uint16_t offset, uint8_t *buf, uint16_t len);

/**
 * @brief  erase entire NVM config area
 * @note   erases all 4 sectors (8KB) of the config area.
 *         after erase, validity magic is lost.
 * @retval NVM_STATUS_OK on success, error code otherwise
 */
nvm_status_t nvm_drv_erase(void);

/**
 * @brief  check if NVM config area has valid magic word
 * @retval 1 = valid, 0 = invalid or erased
 */
uint8_t nvm_drv_is_valid(void);

/**
 * @brief  write validity magic word to config area
 * @note   call this after nvm_drv_erase() + initial data write
 *         to mark the config area as valid.
 * @retval NVM_STATUS_OK on success, error code otherwise
 */
nvm_status_t nvm_drv_set_valid(void);

#ifdef __cplusplus
}
#endif

#endif /* __NVM_DRV_H */
