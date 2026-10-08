/**
  **************************************************************************
  * @file     device_info.h
  * @brief    Device Info (SN) in Flash at 0x0801D000; OTA must not erase
  **************************************************************************
  */
#ifndef __DEVICE_INFO_H
#define __DEVICE_INFO_H

#ifdef __cplusplus
extern "C" {
#endif

#include "at32f422_426.h"

#define DEVICE_INFO_ADDR        0x0801D000U
#define DEVICE_INFO_SIZE        0x1000U
#define DEVICE_INFO_MAGIC       0x44455649U   /* "DEVI" */
#define DEVICE_INFO_VERSION     3U
#define DEVICE_INFO_STRUCT_SIZE 512U
#define DEVICE_INFO_DID_LEN     32U
#define DEVICE_INFO_PUBKEY_LEN  65U

typedef struct
{
  uint32_t magic;
  uint32_t version;
  uint32_t crc32;
  char     sn[32];
  char     hw_version[8];
  uint32_t production_date;
  uint8_t  ecdsa_pubkey[DEVICE_INFO_PUBKEY_LEN]; /* SEC1 uncompressed 04||X||Y */
  uint8_t  pubkey_valid;  /* 0x01 = valid, 0xFF = not provisioned */
  uint16_t qi_fw_version;       /* Qi 芯片固件版本（LE 整数，0~65535） */
  uint8_t  qi_fw_version_valid; /* 0x01=有效，0xFF=未获取 */
  uint8_t  reserved[387];
} device_info_t;

int8_t device_info_read(device_info_t *out);
int8_t device_info_write_sn(const uint8_t *sn32);
int8_t device_info_write_pubkey(const uint8_t *pubkey65);
int8_t device_info_read_qi_version(uint16_t *ver_out);
int8_t device_info_write_qi_version(uint16_t ver);
void   device_info_pad32(uint8_t *dst, const char *src);

#ifdef __cplusplus
}
#endif

#endif /* __DEVICE_INFO_H */
