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

/**
 *  乒乓双扇区（ping-pong）布局（fw 1.1.65 起）：
 *  扇区 A=0x0801F000、扇区 B=0x0801F800，各含一份完整镜像：
 *      [0x000] magic "NVM1"   [0x004] seq(u32)   [0x008] crc32   [0x00C] 保留
 *      [0x100..] 字段区（偏移语义与旧版一致）
 *  写入 = 整镜像写到「非活动」扇区后切换活动指针；擦写窗口内断电只会
 *  牺牲未完成的新副本，旧副本完好——「充电使能过程中断电」不再丢配置。
 *  旧版缺陷（1.1.65 修复）：magic 只有 nvm_drv_set_valid 会写而该函数
 *  无任何调用者 → init 校验永不过 → qi_nvm_load_config 从未加载过数据，
 *  每次重启功率/使能/次数全部回默认。
 */

/* includes ------------------------------------------------------------------*/
#include "nvm_drv.h"
#include <string.h>

/* private define ------------------------------------------------------------*/

/** @brief  bank image header field offsets（相对各扇区基址） */
#define NVM_HDR_MAGIC_OFFSET            0x000U
#define NVM_HDR_SEQ_OFFSET              0x004U
#define NVM_HDR_CRC_OFFSET              0x008U

/** @brief  CRC 覆盖区间：[NVM_HDR_SEQ_OFFSET, 扇区末尾)，计算时 CRC 字段按 0 计 */
#define NVM_CRC_SEED_OFFSET             NVM_HDR_SEQ_OFFSET

/* private variables ---------------------------------------------------------*/

/** @brief  任一扇区镜像有效（magic+crc 通过）则为 1 */
static uint8_t  nvm_initialized = 0;
/** @brief  活动扇区号 0=A 1=B */
static uint8_t  nvm_active_bank = 0;
/** @brief  活动镜像的序号（单调递增，取大者为活动） */
static uint32_t nvm_active_seq = 0;

/* private functions ---------------------------------------------------------*/

/**
 * @brief  扇区基址
 */
static uint32_t bank_base(uint8_t bank)
{
  return NVM_CONFIG_BASE_ADDR + ((uint32_t)bank * NVM_SECTOR_SIZE);
}

/**
 * @brief  软件 CRC32（poly 0xEDB88320，逐位，无查表）
 */
static uint32_t nvm_crc32(const uint8_t *data, uint32_t len)
{
  uint32_t crc = 0xFFFFFFFFU;
  uint32_t i;
  uint8_t  b;

  for (i = 0U; i < len; i++)
  {
    crc ^= data[i];
    for (b = 0U; b < 8U; b++)
    {
      crc = (crc >> 1) ^ (0xEDB88320U & (uint32_t)(0U - (crc & 1U)));
    }
  }
  return ~crc;
}

/**
 * @brief  流式计算某扇区镜像的 CRC（[seq..末尾)，CRC 字段按 0 计）
 */
static uint32_t bank_crc(uint8_t bank)
{
  uint32_t base = bank_base(bank);
  uint32_t crc  = 0xFFFFFFFFU;
  uint32_t off;
  uint8_t  b;
  uint8_t  byte;

  for (off = NVM_CRC_SEED_OFFSET; off < NVM_SECTOR_SIZE; off++)
  {
    byte = *(volatile uint8_t *)(base + off);
    if ((off >= NVM_HDR_CRC_OFFSET) && (off < NVM_HDR_CRC_OFFSET + 4U))
    {
      byte = 0U;  /* CRC 字段自身不参与 */
    }
    crc ^= byte;
    for (b = 0U; b < 8U; b++)
    {
      crc = (crc >> 1) ^ (0xEDB88320U & (uint32_t)(0U - (crc & 1U)));
    }
  }
  return ~crc;
}

/**
 * @brief  读扇区内一个 u32（小端，内存映射直读）
 */
static uint32_t bank_read_u32(uint8_t bank, uint32_t offset)
{
  uint32_t base = bank_base(bank);
  return (uint32_t)*(volatile uint8_t *)(base + offset)
       | ((uint32_t)*(volatile uint8_t *)(base + offset + 1U) << 8)
       | ((uint32_t)*(volatile uint8_t *)(base + offset + 2U) << 16)
       | ((uint32_t)*(volatile uint8_t *)(base + offset + 3U) << 24);
}

/**
 * @brief  校验扇区镜像（magic + crc）
 * @param  bank: 0=A 1=B
 * @param  seq_out: 非空时回传镜像序号
 * @retval 1=有效 0=无效
 */
static uint8_t bank_valid(uint8_t bank, uint32_t *seq_out)
{
  if (bank_read_u32(bank, NVM_HDR_MAGIC_OFFSET) != NVM_VALIDITY_MAGIC)
  {
    return 0U;
  }
  if (bank_read_u32(bank, NVM_HDR_CRC_OFFSET) != bank_crc(bank))
  {
    return 0U;
  }
  if (seq_out != (uint32_t *)0)
  {
    *seq_out = bank_read_u32(bank, NVM_HDR_SEQ_OFFSET);
  }
  return 1U;
}

/**
 * @brief  program a 32-bit word to flash with error checking
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

nvm_status_t nvm_drv_init(void)
{
  uint32_t seq_a = 0U;
  uint32_t seq_b = 0U;
  uint8_t  ok_a;
  uint8_t  ok_b;

  ok_a = bank_valid(0U, &seq_a);
  ok_b = bank_valid(1U, &seq_b);

  if ((ok_a == 0U) && (ok_b == 0U))
  {
    nvm_initialized  = 0U;
    nvm_active_seq   = 0U;
    return NVM_STATUS_NOT_INIT;
  }

  /* 活动镜像 = 有效者中 seq 大者（序号在每次写入时 +1） */
  if ((ok_b != 0U) && ((ok_a == 0U) || (seq_b > seq_a)))
  {
    nvm_active_bank = 1U;
    nvm_active_seq  = seq_b;
  }
  else
  {
    nvm_active_bank = 0U;
    nvm_active_seq  = seq_a;
  }
  nvm_initialized = 1U;
  return NVM_STATUS_OK;
}

uint8_t nvm_drv_is_valid(void)
{
  return nvm_initialized;
}

nvm_status_t nvm_drv_read(uint16_t offset, uint8_t *buf, uint16_t len)
{
  uint32_t addr;
  uint16_t i;

  if (buf == (uint8_t *)0)
  {
    return NVM_STATUS_INVALID_ADDR;
  }

  if ((uint32_t)offset + (uint32_t)len > NVM_SECTOR_SIZE)
  {
    return NVM_STATUS_INVALID_ADDR;
  }

  if (nvm_initialized == 0U)
  {
    return NVM_STATUS_NOT_INIT;
  }

  addr = bank_base(nvm_active_bank) + (uint32_t)offset;
  for (i = 0U; i < len; i++)
  {
    buf[i] = *(volatile uint8_t *)(addr + (uint32_t)i);
  }

  return NVM_STATUS_OK;
}

/**
 * @brief  write data to NVM (ping-pong: 整镜像写非活动扇区后切换)
 * @note   断电窗口内旧副本（当前活动扇区）保持完好。
 *         无有效镜像时以扇区 A 原始内容为种子（兼容 1.1.65 前旧布局的
 *         字段数据，如已保存的功率档位），避免升级后用户设置丢失。
 * @retval NVM_STATUS_OK on success, error code otherwise
 */
nvm_status_t nvm_drv_write(uint16_t offset, uint8_t *buf, uint16_t len)
{
  uint8_t  img[NVM_SECTOR_SIZE];
  uint8_t  target;
  uint32_t seq;
  uint32_t crc;
  uint32_t word;
  uint32_t i;
  nvm_status_t status;

  if (buf == (uint8_t *)0)
  {
    return NVM_STATUS_INVALID_ADDR;
  }

  if (((uint32_t)offset + (uint32_t)len > NVM_SECTOR_SIZE) || (len == 0U))
  {
    return (len == 0U) ? NVM_STATUS_OK : NVM_STATUS_INVALID_ADDR;
  }

  /* 组装目标镜像 */
  if (nvm_initialized != 0U)
  {
    for (i = 0U; i < NVM_SECTOR_SIZE; i++)
    {
      img[i] = *(volatile uint8_t *)(bank_base(nvm_active_bank) + i);
    }
  }
  else
  {
    /* 无有效镜像：以 A 扇区原始内容为种子（迁移旧版字段数据） */
    for (i = 0U; i < NVM_SECTOR_SIZE; i++)
    {
      img[i] = *(volatile uint8_t *)(bank_base(0U) + i);
    }
  }
  memcpy(&img[offset], buf, len);

  seq = nvm_active_seq + 1U;
  img[NVM_HDR_MAGIC_OFFSET]     = (uint8_t)(NVM_VALIDITY_MAGIC & 0xFFU);
  img[NVM_HDR_MAGIC_OFFSET + 1] = (uint8_t)((NVM_VALIDITY_MAGIC >> 8) & 0xFFU);
  img[NVM_HDR_MAGIC_OFFSET + 2] = (uint8_t)((NVM_VALIDITY_MAGIC >> 16) & 0xFFU);
  img[NVM_HDR_MAGIC_OFFSET + 3] = (uint8_t)((NVM_VALIDITY_MAGIC >> 24) & 0xFFU);
  img[NVM_HDR_SEQ_OFFSET]       = (uint8_t)(seq & 0xFFU);
  img[NVM_HDR_SEQ_OFFSET + 1]   = (uint8_t)((seq >> 8) & 0xFFU);
  img[NVM_HDR_SEQ_OFFSET + 2]   = (uint8_t)((seq >> 16) & 0xFFU);
  img[NVM_HDR_SEQ_OFFSET + 3]   = (uint8_t)((seq >> 24) & 0xFFU);
  img[NVM_HDR_CRC_OFFSET]       = 0U;
  img[NVM_HDR_CRC_OFFSET + 1]   = 0U;
  img[NVM_HDR_CRC_OFFSET + 2]   = 0U;
  img[NVM_HDR_CRC_OFFSET + 3]   = 0U;
  crc = nvm_crc32(&img[NVM_CRC_SEED_OFFSET], NVM_SECTOR_SIZE - NVM_CRC_SEED_OFFSET);
  img[NVM_HDR_CRC_OFFSET]       = (uint8_t)(crc & 0xFFU);
  img[NVM_HDR_CRC_OFFSET + 1]   = (uint8_t)((crc >> 8) & 0xFFU);
  img[NVM_HDR_CRC_OFFSET + 2]   = (uint8_t)((crc >> 16) & 0xFFU);
  img[NVM_HDR_CRC_OFFSET + 3]   = (uint8_t)((crc >> 24) & 0xFFU);

  /* 写入非活动扇区（首次写两扇区都空闲时用 B，A 留作旧数据迁移源） */
  target = (nvm_active_bank == 0U) ? 1U : 0U;

  flash_unlock();
  status = flash_erase_sector(bank_base(target));
  if (status != NVM_STATUS_OK)
  {
    flash_lock();
    return status;
  }

  for (i = 0U; i < NVM_SECTOR_SIZE; i += 4U)
  {
    word = (uint32_t)img[i]
         | ((uint32_t)img[i + 1U] << 8)
         | ((uint32_t)img[i + 2U] << 16)
         | ((uint32_t)img[i + 3U] << 24);
    status = flash_write_word(bank_base(target) + i, word);
    if (status != NVM_STATUS_OK)
    {
      flash_lock();
      return status;  /* 新副本残缺=校验不过，旧活动镜像仍有效 */
    }
  }
  flash_lock();

  /* 回读校验通过才切换活动指针 */
  {
    uint32_t rb_seq = 0U;
    if (bank_valid(target, &rb_seq) == 0U)
    {
      return NVM_STATUS_FLASH_ERROR;
    }
  }

  nvm_active_bank = target;
  nvm_active_seq  = seq;
  nvm_initialized = 1U;

  return NVM_STATUS_OK;
}
