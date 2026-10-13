/**
  **************************************************************************
  * @file     nvm_drv.c
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

/* includes ------------------------------------------------------------------*/
#include "nvm_drv.h"
#include <string.h>

/* private define ------------------------------------------------------------*/

/** @brief  number of sectors in config area */
#define NVM_SECTOR_COUNT                (NVM_CONFIG_SIZE / NVM_SECTOR_SIZE)

/** @brief  temporary buffer for read-modify-write operation (one sector) */
#define NVM_SECTOR_BUF_WORDS            (NVM_SECTOR_SIZE / sizeof(uint32_t))

/* private variables ---------------------------------------------------------*/

/** @brief  flag indicating whether config area has been validated */
static uint8_t nvm_initialized = 0;

/* private functions ---------------------------------------------------------*/

/**
 * @brief  get sector base address for a given offset
 * @param  offset: byte offset from config area base
 * @retval sector base address
 */
static uint32_t get_sector_addr(uint16_t offset)
{
  uint32_t sector_index = (uint32_t)offset / NVM_SECTOR_SIZE;
  return NVM_CONFIG_BASE_ADDR + (sector_index * NVM_SECTOR_SIZE);
}

/**
 * @brief  program a 32-bit word to flash with error checking
 * @param  address: target flash address (must be word-aligned)
 * @param  data:    32-bit data to program
 * @retval NVM_STATUS_OK on success, NVM_STATUS_FLASH_ERROR on failure
 */
static nvm_status_t flash_write_word(uint32_t address, uint32_t data)
{
  flash_status_type status;

  status = flash_word_program(address, data);
  if (status != FLASH_OPERATE_DONE)
  {
    return NVM_STATUS_FLASH_ERROR;
  }

  return NVM_STATUS_OK;
}

/**
 * @brief  erase a single flash sector
 * @param  sector_addr: sector start address
 * @retval NVM_STATUS_OK on success, NVM_STATUS_FLASH_ERROR on failure
 */
static nvm_status_t flash_erase_sector(uint32_t sector_addr)
{
  flash_status_type status;

  status = flash_sector_erase(sector_addr);
  if (status != FLASH_OPERATE_DONE)
  {
    return NVM_STATUS_FLASH_ERROR;
  }

  return NVM_STATUS_OK;
}

/* exported functions --------------------------------------------------------*/

/**
 * @brief  initialize NVM driver and check config area validity
 * @note   checks for validity magic word at base address.
 *         if magic is not found, the config area is considered uninitialized.
 * @retval NVM_STATUS_OK if config area is valid, NVM_STATUS_NOT_INIT otherwise
 */
nvm_status_t nvm_drv_init(void)
{
  uint32_t magic;

  /* read magic word from config area */
  magic = *(volatile uint32_t *)NVM_CONFIG_BASE_ADDR;

  if (magic == NVM_VALIDITY_MAGIC)
  {
    nvm_initialized = 1;
    return NVM_STATUS_OK;
  }

  nvm_initialized = 0;
  return NVM_STATUS_NOT_INIT;
}

/**
 * @brief  check if NVM config area has valid magic word
 * @retval 1 = valid, 0 = invalid or erased
 */
uint8_t nvm_drv_is_valid(void)
{
  return nvm_initialized;
}

/**
 * @brief  write validity magic word to config area
 * @note   call this after nvm_drv_erase() + initial data write
 *         to mark the config area as valid.
 * @retval NVM_STATUS_OK on success, error code otherwise
 */
nvm_status_t nvm_drv_set_valid(void)
{
  nvm_status_t status;

  flash_unlock();

  status = flash_write_word(NVM_CONFIG_BASE_ADDR + NVM_VALIDITY_OFFSET, NVM_VALIDITY_MAGIC);
  if (status == NVM_STATUS_OK)
  {
    nvm_initialized = 1;
  }

  flash_lock();

  return status;
}

/**
 * @brief  read data from NVM config area
 * @param  offset: byte offset from config area base (0 ~ NVM_CONFIG_SIZE-1)
 * @param  buf:    pointer to destination buffer
 * @param  len:    number of bytes to read
 * @retval NVM_STATUS_OK on success, error code otherwise
 */
nvm_status_t nvm_drv_read(uint16_t offset, uint8_t *buf, uint16_t len)
{
  uint32_t addr;
  uint16_t i;

  /* validate parameters */
  if (buf == (uint8_t *)0)
  {
    return NVM_STATUS_INVALID_ADDR;
  }

  if ((uint32_t)offset + (uint32_t)len > NVM_CONFIG_SIZE)
  {
    return NVM_STATUS_INVALID_ADDR;
  }

  /* calculate absolute address */
  addr = NVM_CONFIG_BASE_ADDR + (uint32_t)offset;

  /* read byte by byte (flash is memory-mapped, direct read is safe) */
  for (i = 0; i < len; i++)
  {
    buf[i] = *(volatile uint8_t *)(addr + (uint32_t)i);
  }

  return NVM_STATUS_OK;
}

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
nvm_status_t nvm_drv_write(uint16_t offset, uint8_t *buf, uint16_t len)
{
  uint32_t sector_addr;
  uint32_t word_offset;
  uint32_t word_data;
  uint16_t remaining;
  uint16_t write_len;
  uint16_t sector_offset;
  uint8_t  sector_buf[NVM_SECTOR_SIZE];
  uint8_t  *src_ptr;
  nvm_status_t status;
  uint16_t i;
  uint8_t  need_erase;

  /* validate parameters */
  if (buf == (uint8_t *)0)
  {
    return NVM_STATUS_INVALID_ADDR;
  }

  if ((uint32_t)offset + (uint32_t)len > NVM_CONFIG_SIZE)
  {
    return NVM_STATUS_INVALID_ADDR;
  }

  if (len == 0)
  {
    return NVM_STATUS_OK;
  }

  flash_unlock();

  src_ptr  = buf;
  remaining = len;

  /* process sector by sector */
  while (remaining > 0)
  {
    /* determine which sector this offset falls into */
    sector_addr   = get_sector_addr(offset);
    sector_offset = offset % NVM_SECTOR_SIZE;

    /* calculate how many bytes to write in this sector */
    write_len = NVM_SECTOR_SIZE - sector_offset;
    if (write_len > remaining)
    {
      write_len = remaining;
    }

    /* check if we need to erase (write region doesn't cover full sector) */
    need_erase = 0;
    if ((sector_offset != 0) || (write_len != NVM_SECTOR_SIZE))
    {
      /* partial sector write: need read-modify-write */
      need_erase = 1;

      /* read existing sector data */
      for (i = 0; i < NVM_SECTOR_SIZE; i++)
      {
        sector_buf[i] = *(volatile uint8_t *)(sector_addr + (uint32_t)i);
      }

      /* overlay new data */
      for (i = 0; i < write_len; i++)
      {
        sector_buf[sector_offset + i] = src_ptr[i];
      }
    }
    else
    {
      /* full sector write: still need erase first */
      need_erase = 1;

      /* copy source data to sector buffer */
      for (i = 0; i < NVM_SECTOR_SIZE; i++)
      {
        sector_buf[i] = src_ptr[i];
      }
    }

    if (need_erase)
    {
      /* erase the sector */
      status = flash_erase_sector(sector_addr);
      if (status != NVM_STATUS_OK)
      {
        flash_lock();
        return status;
      }

      /* write back the sector data word by word */
      for (word_offset = 0; word_offset < NVM_SECTOR_SIZE; word_offset += 4)
      {
        /* pack 4 bytes into a 32-bit word (little-endian) */
        word_data = (uint32_t)sector_buf[word_offset]
                  | ((uint32_t)sector_buf[word_offset + 1] << 8)
                  | ((uint32_t)sector_buf[word_offset + 2] << 16)
                  | ((uint32_t)sector_buf[word_offset + 3] << 24);

        status = flash_write_word(sector_addr + word_offset, word_data);
        if (status != NVM_STATUS_OK)
        {
          flash_lock();
          return status;
        }
      }
    }

    /* advance to next sector */
    offset    += write_len;
    src_ptr   += write_len;
    remaining -= write_len;
  }

  flash_lock();

  return NVM_STATUS_OK;
}

/**
 * @brief  erase entire NVM config area
 * @note   erases all sectors of the config area (4KB = 1 sector).
 *         after erase, validity magic is lost.
 * @retval NVM_STATUS_OK on success, error code otherwise
 */
nvm_status_t nvm_drv_erase(void)
{
  uint32_t sector_addr;
  nvm_status_t status;
  uint8_t i;

  flash_unlock();

  /* erase each sector in the config area */
  for (i = 0; i < NVM_SECTOR_COUNT; i++)
  {
    sector_addr = NVM_CONFIG_BASE_ADDR + ((uint32_t)i * NVM_SECTOR_SIZE);

    status = flash_erase_sector(sector_addr);
    if (status != NVM_STATUS_OK)
    {
      flash_lock();
      return status;
    }
  }

  flash_lock();

  /* mark as uninitialized */
  nvm_initialized = 0;

  return NVM_STATUS_OK;
}
