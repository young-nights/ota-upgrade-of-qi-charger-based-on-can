/**
  **************************************************************************
  * @file     device_info.c
  * @brief    Device Info Flash read/write (DEVI + CRC32)
  **************************************************************************
  */
#include "device_info.h"
#include "at32f422_426_conf.h"
#include <string.h>

static uint32_t di_crc_struct(const device_info_t *info)
{
  const uint8_t *p = (const uint8_t *)info;
  uint32_t crc = 0xFFFFFFFFU;
  uint32_t j;
  uint32_t bit;
  uint32_t n;

  /* skip crc32 field at offset 8 */
  for (n = 0U; n < DEVICE_INFO_STRUCT_SIZE; n++)
  {
    if ((n >= 8U) && (n < 12U))
    {
      continue;
    }
    crc ^= (uint32_t)p[n];
    for (j = 0; j < 8U; j++)
    {
      bit = crc & 1U;
      crc >>= 1;
      if (bit != 0U)
      {
        crc ^= 0xEDB88320U;
      }
    }
  }
  return crc ^ 0xFFFFFFFFU;
}

void device_info_pad32(uint8_t *dst, const char *src)
{
  uint8_t i;
  for (i = 0U; i < DEVICE_INFO_DID_LEN; i++)
  {
    dst[i] = 0x20U;
  }
  if (src == (const char *)0)
  {
    return;
  }
  for (i = 0U; (i < DEVICE_INFO_DID_LEN) && (src[i] != '\0'); i++)
  {
    dst[i] = (uint8_t)src[i];
  }
}

int8_t device_info_read(device_info_t *out)
{
  const device_info_t *flash = (const device_info_t *)DEVICE_INFO_ADDR;

  if (out == (device_info_t *)0)
  {
    return -1;
  }
  memcpy((void *)out, (const void *)flash, sizeof(device_info_t));
  if (out->magic != DEVICE_INFO_MAGIC)
  {
    return -1;
  }
  if (out->version != DEVICE_INFO_VERSION)
  {
    return -1;
  }
  if (di_crc_struct(out) != out->crc32)
  {
    return -1;
  }
  return 0;
}

int8_t device_info_write_sn(const uint8_t *sn32)
{
  device_info_t info;
  const uint32_t *src;
  uint32_t words;
  uint32_t i;
  flash_status_type st;

  if (sn32 == (const uint8_t *)0)
  {
    return -1;
  }

  if (device_info_read(&info) != 0)
  {
    memset((void *)&info, 0xFF, sizeof(info));
    info.magic            = DEVICE_INFO_MAGIC;
    info.version          = DEVICE_INFO_VERSION;
    info.production_date  = 0U;
    /* hw_version 已不作为版本来源（F193 恒读 HW_VERSION_STR），此处置零仅为初始化保留字段 */
    memset((void *)info.hw_version, 0, sizeof(info.hw_version));
    info.pubkey_valid     = 0xFFU;
    memset((void *)info.reserved, 0xFF, sizeof(info.reserved));
  }

  memcpy((void *)info.sn, (const void *)sn32, 32U);
  info.crc32 = di_crc_struct(&info);

  flash_unlock();
  st = flash_sector_erase(DEVICE_INFO_ADDR);
  if (st != FLASH_OPERATE_DONE)
  {
    flash_lock();
    return -1;
  }

  src   = (const uint32_t *)&info;
  words = sizeof(device_info_t) / 4U;
  for (i = 0U; i < words; i++)
  {
    st = flash_word_program(DEVICE_INFO_ADDR + (i * 4U), src[i]);
    if (st != FLASH_OPERATE_DONE)
    {
      flash_lock();
      return -1;
    }
  }
  flash_lock();
  return 0;
}

int8_t device_info_write_pubkey(const uint8_t *pubkey65)
{
  device_info_t info;
  const uint32_t *src;
  uint32_t words;
  uint32_t i;
  flash_status_type st;

  if (pubkey65 == (const uint8_t *)0)
  {
    return -1;
  }
  if (pubkey65[0] != 0x04U)
  {
    return -1;  /* SEC1 uncompressed tag */
  }

  if (device_info_read(&info) != 0)
  {
    memset((void *)&info, 0xFF, sizeof(info));
    info.magic            = DEVICE_INFO_MAGIC;
    info.version          = DEVICE_INFO_VERSION;
    info.production_date  = 0U;
    /* hw_version 已不作为版本来源（F193 恒读 HW_VERSION_STR），此处置零仅为初始化保留字段 */
    memset((void *)info.hw_version, 0, sizeof(info.hw_version));
    memset((void *)info.reserved, 0xFF, sizeof(info.reserved));
  }

  memcpy((void *)info.ecdsa_pubkey, (const void *)pubkey65, DEVICE_INFO_PUBKEY_LEN);
  info.pubkey_valid = 0x01U;
  info.crc32 = di_crc_struct(&info);

  flash_unlock();
  st = flash_sector_erase(DEVICE_INFO_ADDR);
  if (st != FLASH_OPERATE_DONE)
  {
    flash_lock();
    return -1;
  }

  src   = (const uint32_t *)&info;
  words = sizeof(device_info_t) / 4U;
  for (i = 0U; i < words; i++)
  {
    st = flash_word_program(DEVICE_INFO_ADDR + (i * 4U), src[i]);
    if (st != FLASH_OPERATE_DONE)
    {
      flash_lock();
      return -1;
    }
  }
  flash_lock();
  return 0;
}
