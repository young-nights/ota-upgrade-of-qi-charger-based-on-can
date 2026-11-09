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
 * @note   AT32F426 with 128KB Flash uses 2KB sectors。
 *         fw 1.1.65 起为乒乓双扇区：A=0x0801F000、B=0x0801F800 各存一份
 *         完整镜像（[0x000]magic [0x004]seq [0x008]crc32 [0x100..]字段）。
 *         写入=整镜像写非活动扇区后切换，擦写窗口断电不丢配置。
 *         字段偏移（NVM_OFFSET_*，见 can_protocol.h）相对扇区基址、
 *         两扇区一致，与旧版单扇区布局兼容。
 */
#define NVM_CONFIG_BASE_ADDR            0x0801F000U  /*!< config area start address */
#define NVM_CONFIG_END_ADDR             0x0801FFFFU  /*!< config area end address */
#define NVM_CONFIG_SIZE                 0x1000U      /*!< config area size: 4KB */
#define NVM_SECTOR_SIZE                 0x800U       /*!< sector size: 2KB for AT32F426 */
#define NVM_VALIDITY_MAGIC              0x4E564D31U  /*!< magic: "NVM1" in ASCII */

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
 * @brief  read data from NVM config area (active bank)
 * @param  offset: byte offset within the bank image (0 ~ NVM_SECTOR_SIZE-1)
 * @param  buf:    pointer to destination buffer
 * @param  len:    number of bytes to read
 * @retval NVM_STATUS_OK on success, error code otherwise
 */
nvm_status_t nvm_drv_read(uint16_t offset, uint8_t *buf, uint16_t len);

/**
 * @brief  write data to NVM config area (ping-pong)
 * @note   整镜像写入非活动扇区后切换活动指针；擦写窗口内断电时旧副本
 *         保持完好。未写区域沿用当前镜像内容。
 * @param  offset: byte offset within the bank image (0 ~ NVM_SECTOR_SIZE-1)
 * @param  buf:    pointer to source data buffer
 * @param  len:    number of bytes to write
 * @retval NVM_STATUS_OK on success, error code otherwise
 */
nvm_status_t nvm_drv_write(uint16_t offset, uint8_t *buf, uint16_t len);

/**
 * @brief  check if NVM config area has a valid image
 * @retval 1 = valid, 0 = invalid or erased
 */
uint8_t nvm_drv_is_valid(void);

#ifdef __cplusplus
}
#endif

#endif /* __NVM_DRV_H */
