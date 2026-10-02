/**
 **************************************************************************
 * @file     ota_download.c
 * @brief    UDS 0x31/0x34/0x36/0x37 download into the Backup region
 *           (Boot + App architecture, OTA-ARCH-0920)
 **************************************************************************
 *
 * Flow: host enters programming session + security unlock -> 0x31 erases
 * the Backup region (0x08010000..0x0801BFFF) -> 0x34/0x36 stream the
 * XATO image (linked for the App window) into Backup -> 0x37 flushes,
 * verifies the staged image (magic/length/CRC/ECDSA + reset vector in the
 * App run window), commits metadata (backup_valid=1 + backup_crc32),
 * responds 0x77, then NVIC_SystemReset. BOOT performs the physical
 * Backup->App copy on the next boot.
 *
 * The APP never erases the region it executes from; the pending flag is
 * cleared by BOOT only after the copy re-verifies (power-loss safe).
 */

#include "ota_download.h"
#include "ota_trigger.h"
#include "can_protocol.h"
#include "can_driver.h"
#include "sit1145.h"
#include "timer_drv.h"
#include "lifecycle.h"
#include "device_info.h"
#include "sha256.h"
#include "uECC.h"
#include "at32f422_426_flash.h"
#include "at32f422_426.h"
#include <string.h>

/* VERIFY ENFORCE SWITCH: 1 = strict check (normal), 0 = force pass on failure
 * (TEMPORARY for testing, added 2026-09-23; re-enabled=1 on 2026-10-02 per
 * user instruction: 验签失能屏蔽必须解除) */
#define OTA_VERIFY_ENFORCE  1

#if (OTA_VERIFY_ENFORCE == 0)
/* Bypass-hit marker (debugger-watchable): nonzero = at least one verify
 * failure was force-passed. Distinguishes a real pass (0) from a
 * failed-but-passed run. Warning text of the bypass event:
 * "verify FAILED but ENFORCE=0, force pass" (no text logger on this
 * MCU). */
static volatile uint8_t g_verify_bypass_hit = 0U;
#endif

#define FLASH_PHYSICAL_END  0x08020000U
#define VERIFY_CHUNK        256U

/* Bounded erase budget (TC-0507 hardening): the library erase timeout is
 * effectively unbounded; a wedged flash BUSY with IRQs masked would hang
 * the whole tree. ~25ms/sector typical -> 4M polls ~4x margin. Healthy
 * path unaffected; wedged path degrades to bounded timeout -> NRC 0x72. */
#define OTA_ERASE_POLLS_MAX  4000000U

static flash_status_type flash_sector_erase_bounded(uint32_t sector_address,
                                                    uint32_t max_polls)
{
  flash_status_type status;

  FLASH->ctrl_bit.secers = TRUE;
  FLASH->addr = sector_address;
  FLASH->ctrl_bit.erstr = TRUE;
  status = flash_operation_wait_for(max_polls);
  FLASH->ctrl_bit.secers = FALSE;
  return status;
}

static uint8_t  g_erased;
static uint8_t  g_active;
static uint8_t  g_trial_ready;    /* commit done, awaiting reset (idempotence guard) */
static uint8_t  g_target;         /* staging marker reported via DID 0x2114 */
static uint32_t g_base;           /* Backup region base */
static uint32_t g_size;           /* Backup region size */
static uint32_t g_write_addr;
static uint32_t g_bytes_written;
static uint32_t g_expected_size;
static uint8_t  g_block_seq;
static uint8_t  g_pad[4];
static uint8_t  g_pad_len;
static uint8_t  g_exit_pending;

void ota_dl_abort(void)
{
  g_erased = 0U;
  g_active = 0U;
  g_exit_pending = 0U;
  g_pad_len = 0U;
  g_block_seq = 0U;
  g_bytes_written = 0U;
  g_expected_size = 0U;
}

uint8_t ota_dl_target_slot(void)
{
  /* deprecated name; reports the staging target marker (0x02 = Backup) */
  return g_target;
}

uint8_t ota_dl_erased(void)
{
  return g_erased;
}

uint8_t ota_dl_active(void)
{
  return g_active;
}

uint8_t ota_dl_trial_ready(void)
{
  /* deprecated name; nonzero after a successful 0x37 commit */
  return g_trial_ready;
}

static uint8_t program_words(const uint8_t *src, uint16_t src_len)
{
  uint16_t n = src_len;
  uint16_t idx = 0U;
  flash_status_type st;

  while (n > 0U)
  {
    while ((g_pad_len < 4U) && (n > 0U))
    {
      g_pad[g_pad_len++] = src[idx++];
      n--;
    }
    if (g_pad_len < 4U)
    {
      break;
    }
    if ((g_bytes_written + 4U) > g_size)
    {
      return 1U;
    }
    {
      uint32_t word;
      word  =  (uint32_t)g_pad[0];
      word |= ((uint32_t)g_pad[1] << 8);
      word |= ((uint32_t)g_pad[2] << 16);
      word |= ((uint32_t)g_pad[3] << 24);
      st = flash_word_program(g_write_addr, word);
      if (st != FLASH_OPERATE_DONE)
      {
        return 1U;
      }
    }
    g_write_addr += 4U;
    g_bytes_written += 4U;
    g_pad_len = 0U;
  }
  return 0U;
}

static uint8_t program_flush(void)
{
  uint32_t word;
  uint8_t k;
  flash_status_type st;

  if (g_pad_len == 0U)
  {
    return 0U;
  }
  word = 0xFFFFFFFFU;
  for (k = 0U; k < g_pad_len; k++)
  {
    word &= ~((uint32_t)0xFFU << (k * 8U));
    word |= ((uint32_t)g_pad[k] << (k * 8U));
  }
  st = flash_word_program(g_write_addr, word);
  if (st != FLASH_OPERATE_DONE)
  {
    return 1U;
  }
  g_write_addr += (uint32_t)g_pad_len;
  g_bytes_written += (uint32_t)g_pad_len;
  g_pad_len = 0U;
  return 0U;
}

static uint8_t s_di_pubkey[DEVICE_INFO_PUBKEY_LEN];

static const uint8_t *image_pubkey(void)
{
  device_info_t di;

  if (device_info_read(&di) == 0)
  {
    if ((di.pubkey_valid == 0x01U) && (di.ecdsa_pubkey[0] == 0x04U))
    {
      memcpy(s_di_pubkey, di.ecdsa_pubkey, DEVICE_INFO_PUBKEY_LEN);
      return s_di_pubkey;
    }
  }
  if (g_app_ecdsa_pubkey[0] == 0x04U)
  {
    return g_app_ecdsa_pubkey;
  }
  return (const uint8_t *)0;
}

/**
 * @brief  verify the XATO image staged in the Backup region
 * @note   reset vector must target the App run window
 *         [0x08004100, 0x08010000) — staged images are App-linked; this
 *         rejects images built for any other base before the flag is set
 */
static int8_t verify_backup_image(void)
{
  const ota_image_header_t *hdr = (const ota_image_header_t *)g_base;
  const uint8_t *payload;
  uint32_t max_len;
  uint32_t crc;
  const uint8_t *pk;
  uint8_t hash[32];
  sha256_ctx_t ctx;
  uint32_t ofs;
  uint32_t n;
  uint32_t reset;

  if (hdr->magic != OTA_IMAGE_MAGIC)
  {
    return -1;
  }
  max_len = g_size - OTA_IMAGE_HEADER_SIZE;
  if ((hdr->image_length == 0U) || (hdr->image_length > max_len))
  {
    return -1;
  }
  payload = (const uint8_t *)(g_base + OTA_IMAGE_HEADER_SIZE);
  crc = ota_crc32(payload, hdr->image_length);
  if (crc != hdr->crc32)
  {
    return -1;
  }
  reset = (*(const uint32_t *)(g_base + OTA_IMAGE_HEADER_SIZE + 4U)) & 0xFFFFFFFEU;
  if ((reset < OTA_APP_ENTRY_ADDR) ||
      (reset >= (OTA_APP_BASE_ADDR + OTA_APP_SIZE)))
  {
    return -1;
  }
  pk = image_pubkey();
  if (pk == (const uint8_t *)0)
  {
    return -1;
  }
  sha256_init(&ctx);
  ofs = 0U;
  while (ofs < hdr->image_length)
  {
    n = hdr->image_length - ofs;
    if (n > VERIFY_CHUNK)
    {
      n = VERIFY_CHUNK;
    }
    sha256_update(&ctx, payload + ofs, n);
    ofs += n;
    can_proto_pump_long_op();  /* 时间闸门：仅距上次 0x78 超过 4500ms 才补发 */
  }
  sha256_final(&ctx, hash);
  if (uECC_verify(pk, hash, hdr->signature) != 1)
  {
    return -1;
  }
  return 0;
}

/**
 * @brief  commit the staged image: flag backup_valid + record payload CRC
 * @note   BOOT clears the flag only after the copy re-verifies
 */
static int8_t commit_backup(void)
{
  ota_metadata_t meta;
  const ota_image_header_t *hdr = (const ota_image_header_t *)g_base;

  if (ota_metadata_read(&meta) != 0)
  {
    memset(&meta, 0, sizeof(meta));
    meta.magic = OTA_META_MAGIC;
    meta.version = OTA_META_VERSION;
  }

  meta.backup_valid = 1U;
  meta.backup_crc32 = hdr->crc32;
  meta.ota_state    = OTA_STATE_IDLE;
  return ota_metadata_save(&meta);
}

void ota_dl_handle_erase(uint8_t *data, uint16_t len)
{
  uint16_t rid;
  uint8_t sub;
  uint32_t addr;
  uint8_t resp[4];
  ota_metadata_t meta;

  if (!can_proto_in_programming())
  {
    can_proto_send_nrc(UDS_SID_ROUTINE_CONTROL, UDS_NRC_CONDITIONS_NOT_CORRECT);
    return;
  }
  if (!can_proto_security_unlocked())
  {
    can_proto_send_nrc(UDS_SID_ROUTINE_CONTROL, UDS_NRC_SECURITY_ACCESS_DENIED);
    return;
  }
  if (len < 4U)
  {
    can_proto_send_nrc(UDS_SID_ROUTINE_CONTROL, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
    return;
  }
  sub = data[1];
  rid = ((uint16_t)data[2] << 8) | (uint16_t)data[3];
  if ((sub != 0x01U) || (rid != ROUTINE_ERASE_MEMORY))
  {
    can_proto_send_nrc(UDS_SID_ROUTINE_CONTROL, UDS_NRC_REQUEST_OUT_OF_RANGE);
    return;
  }

  g_trial_ready = 0U;
  ota_dl_abort();
  g_base   = OTA_BACKUP_BASE_ADDR;
  g_size   = OTA_BACKUP_SIZE;
  g_target = OTA_DL_TARGET_BACKUP;

  /* active-region protection: never erase the region we execute from */
  if (g_base == OTA_APP_BASE_ADDR)
  {
    can_proto_send_nrc(UDS_SID_ROUTINE_CONTROL, UDS_NRC_CONDITIONS_NOT_CORRECT);
    return;
  }
  if ((g_base < OTA_APP_BASE_ADDR) ||
      ((g_base + g_size) > FLASH_PHYSICAL_END) ||
      ((g_base + g_size) > OTA_META_PRIMARY_ADDR))
  {
    can_proto_send_nrc(UDS_SID_ROUTINE_CONTROL, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
    return;
  }

  can_proto_begin_long_op(UDS_SID_ROUTINE_CONTROL);
  for (addr = g_base; addr < (g_base + g_size); addr += OTA_FLASH_SECTOR_SIZE)
  {
    flash_status_type st;

    /* Single-bank: IRQ fetch during sector erase wedges the core. */
    __disable_irq();
    flash_unlock();
    st = flash_sector_erase_bounded(addr, OTA_ERASE_POLLS_MAX);
    flash_lock();
    __enable_irq();
    if (st != FLASH_OPERATE_DONE)
    {
      /* bounded timeout / erase error: observable failure, retry-safe */
      can_proto_end_long_op();
      can_proto_send_nrc(UDS_SID_ROUTINE_CONTROL, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
      return;
    }
    can_proto_pump_long_op();
  }

  /* pump one 0x78 before metadata writes (silent stretch protection) */
  can_proto_pump_long_op();

  if (ota_metadata_read(&meta) == 0)
  {
    /* stale pending flag from an interrupted earlier upgrade: the staged
     * data is about to be overwritten, so drop the flag now; BOOT copy
     * only ever runs on flags committed after a successful 0x37 */
    meta.backup_valid = 0U;
    meta.ota_state = OTA_STATE_DOWNLOADING;
    (void)ota_metadata_save(&meta);
  }

  g_erased = 1U;
  g_write_addr = g_base;
  g_bytes_written = 0U;
  g_pad_len = 0U;
  g_block_seq = 0U;
  can_proto_end_long_op();
  resp[0] = (uint8_t)(UDS_SID_ROUTINE_CONTROL + UDS_POSITIVE_RESPONSE_OFFSET);
  resp[1] = sub;
  resp[2] = data[2];
  resp[3] = data[3];
  can_proto_send_response(resp, 4);
  (void)can_driver_wait_tx_idle(50U);
}

void ota_dl_handle_request_download(uint8_t *data, uint16_t len)
{
  uint8_t resp[4];
  uint8_t alfid;
  uint8_t addr_n;
  uint8_t size_n;
  uint32_t mem_size;
  uint32_t dl_addr;
  uint8_t i;

  if (!can_proto_in_programming())
  {
    can_proto_send_nrc(UDS_SID_REQUEST_DOWNLOAD, UDS_NRC_CONDITIONS_NOT_CORRECT);
    return;
  }
  if (!can_proto_security_unlocked())
  {
    can_proto_send_nrc(UDS_SID_REQUEST_DOWNLOAD, UDS_NRC_SECURITY_ACCESS_DENIED);
    return;
  }
  if (g_erased == 0U)
  {
    can_proto_send_nrc(UDS_SID_REQUEST_DOWNLOAD, UDS_NRC_REQUEST_SEQUENCE_ERROR);
    return;
  }
  if (len < 3U)
  {
    can_proto_send_nrc(UDS_SID_REQUEST_DOWNLOAD, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
    return;
  }
  alfid = data[2];
  addr_n = (uint8_t)((alfid >> 4) & 0x0FU);
  size_n = (uint8_t)(alfid & 0x0FU);
  if ((addr_n == 0U) || (size_n == 0U) || (addr_n > 4U) || (size_n > 4U) ||
      (len < (uint16_t)(3U + addr_n + size_n)))
  {
    can_proto_send_nrc(UDS_SID_REQUEST_DOWNLOAD, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
    return;
  }
  if (addr_n > 0U)
  {
    dl_addr = 0U;
    for (i = 0U; i < addr_n; i++)
    {
      dl_addr = (dl_addr << 8) | (uint32_t)data[3U + i];
    }
    /* single target: the Backup region base */
    if (dl_addr != g_base)
    {
      can_proto_send_nrc(UDS_SID_REQUEST_DOWNLOAD, UDS_NRC_REQUEST_OUT_OF_RANGE);
      return;
    }
  }
  mem_size = 0U;
  for (i = 0U; i < size_n; i++)
  {
    mem_size = (mem_size << 8) | (uint32_t)data[3U + addr_n + i];
  }
  if ((mem_size == 0U) || (mem_size > g_size))
  {
    can_proto_send_nrc(UDS_SID_REQUEST_DOWNLOAD, UDS_NRC_REQUEST_OUT_OF_RANGE);
    return;
  }
  g_expected_size = mem_size;
  g_write_addr = g_base;
  g_bytes_written = 0U;
  g_pad_len = 0U;
  g_block_seq = 0U;
  g_active = 1U;
  g_trial_ready = 0U;
  resp[0] = (uint8_t)(UDS_SID_REQUEST_DOWNLOAD + UDS_POSITIVE_RESPONSE_OFFSET);
  resp[1] = 0x20U;
  resp[2] = (uint8_t)((OTA_DL_MAX_BLOCK_LEN >> 8) & 0xFFU);
  resp[3] = (uint8_t)(OTA_DL_MAX_BLOCK_LEN & 0xFFU);
  can_proto_send_response(resp, 4);
}

void ota_dl_handle_transfer_data(uint8_t *data, uint16_t len)
{
  uint8_t bsc;
  uint16_t dlen;
  uint8_t resp[2];

  if (!can_proto_in_programming())
  {
    can_proto_send_nrc(UDS_SID_TRANSFER_DATA, UDS_NRC_CONDITIONS_NOT_CORRECT);
    return;
  }
  if (!can_proto_security_unlocked())
  {
    can_proto_send_nrc(UDS_SID_TRANSFER_DATA, UDS_NRC_SECURITY_ACCESS_DENIED);
    return;
  }
  if (g_active == 0U)
  {
    can_proto_send_nrc(UDS_SID_TRANSFER_DATA, UDS_NRC_REQUEST_SEQUENCE_ERROR);
    return;
  }
  if (len < 3U)
  {
    can_proto_send_nrc(UDS_SID_TRANSFER_DATA, UDS_NRC_INCORRECT_MESSAGE_LENGTH);
    return;
  }
  bsc = data[1];
  if ((g_block_seq != 0U) && (bsc == g_block_seq))
  {
    resp[0] = (uint8_t)(UDS_SID_TRANSFER_DATA + UDS_POSITIVE_RESPONSE_OFFSET);
    resp[1] = bsc;
    can_proto_send_response(resp, 2);
    return;
  }
  g_block_seq++;
  if (g_block_seq == 0U)
  {
    g_block_seq = 1U;
  }
  if (bsc != g_block_seq)
  {
    g_active = 0U;
    can_proto_send_nrc(UDS_SID_TRANSFER_DATA, UDS_NRC_WRONG_BLOCK_SEQUENCE);
    return;
  }
  dlen = (uint16_t)(len - 2U);
  if ((g_bytes_written + g_pad_len + (uint32_t)dlen) > g_size)
  {
    g_active = 0U;
    can_proto_send_nrc(UDS_SID_TRANSFER_DATA, UDS_NRC_TRANSFER_DATA_ABORTED);
    return;
  }
  flash_unlock();
  if (program_words(&data[2], dlen) != 0U)
  {
    g_active = 0U;
    flash_lock();
    can_proto_send_nrc(UDS_SID_TRANSFER_DATA, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
    return;
  }
  flash_lock();
  resp[0] = (uint8_t)(UDS_SID_TRANSFER_DATA + UDS_POSITIVE_RESPONSE_OFFSET);
  resp[1] = bsc;
  can_proto_send_response(resp, 2);
  (void)can_driver_wait_tx_idle(20U);
}

void ota_dl_handle_transfer_exit(uint8_t *data, uint16_t len)
{
  uint8_t resp[1];

  (void)data;
  (void)len;
  if (!can_proto_in_programming())
  {
    can_proto_send_nrc(UDS_SID_TRANSFER_EXIT, UDS_NRC_CONDITIONS_NOT_CORRECT);
    return;
  }
  if (g_exit_pending != 0U)
  {
    can_proto_pump_long_op();  /* 时间闸门：重复 0x37 不再洪泛 0x78 */
    return;
  }
  if (g_active == 0U)
  {
    if (g_trial_ready != 0U)
    {
      /* commit already done; idempotent positive for retried 0x37
       * before the reset lands */
      resp[0] = (uint8_t)(UDS_SID_TRANSFER_EXIT + UDS_POSITIVE_RESPONSE_OFFSET);
      can_proto_send_response(resp, 1);
    }
    else
    {
      can_proto_send_nrc(UDS_SID_TRANSFER_EXIT, UDS_NRC_TRANSFER_DATA_ABORTED);
    }
    return;
  }
  if (!can_proto_security_unlocked())
  {
    can_proto_send_nrc(UDS_SID_TRANSFER_EXIT, UDS_NRC_SECURITY_ACCESS_DENIED);
    return;
  }
  g_active = 0U;
  g_exit_pending = 1U;
  can_proto_begin_long_op(UDS_SID_TRANSFER_EXIT);
}

void ota_dl_poll(void)
{
  uint8_t resp[1];
  uint8_t h_before;
  uint8_t h_after;
  uint8_t h_bc0;
  uint8_t have_prev;
  uint32_t t0;

  if (g_exit_pending == 0U)
  {
    return;
  }
  g_exit_pending = 0U;

  flash_unlock();
  if (program_flush() != 0U)
  {
    flash_lock();
    can_proto_end_long_op();
    can_proto_send_nrc(UDS_SID_TRANSFER_EXIT, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
    return;
  }
  flash_lock();

  if ((g_expected_size != 0U) && (g_bytes_written != g_expected_size))
  {
    can_proto_end_long_op();
    can_proto_send_nrc(UDS_SID_TRANSFER_EXIT, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
    return;
  }
  if (verify_backup_image() != 0)
  {
#if (OTA_VERIFY_ENFORCE == 0)
    /* verify FAILED but ENFORCE=0: force pass -> commit_backup -> 0x77 ->
     * auto reset as if verify passed (verify computation above ran in
     * full, only the rejection is bypassed). Single switch controls all
     * bypass logic; the three other failure paths stay untouched. */
    g_verify_bypass_hit = 1U;
#else
    can_proto_end_long_op();
    can_proto_send_nrc(UDS_SID_TRANSFER_EXIT, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
    return;
#endif
  }
  if (commit_backup() != 0)
  {
    can_proto_end_long_op();
    can_proto_send_nrc(UDS_SID_TRANSFER_EXIT, UDS_NRC_GENERAL_PROGRAMMING_FAILURE);
    return;
  }
  g_erased = 0U;
  g_trial_ready = 1U;
  can_proto_end_long_op();
  resp[0] = (uint8_t)(UDS_SID_TRANSFER_EXIT + UDS_POSITIVE_RESPONSE_OFFSET);
  /* 0x37 finish: after verify + commit_backup succeed the APP resets
   * itself -- upgrade completion triggers reset directly, no host
   * request needed (11 01 ECUReset service removed 2026-09-23). On the
   * next boot the BOOT copies Backup->App and re-verifies.
   * g_trial_ready=1 keeps a retried 0x37 before the reset idempotent
   * (positive response, no double commit).
   *
   * Frame-dispatch race guard (same triple pattern as the removed
   * handle_ecu_reset): the generic TX-idle check can be satisfied by a
   * previous frame / empty mailbox while the target frame is still
   * queued, and an immediate reset would kill the frame (the "51 01
   * lost" race recorded in can_driver.c). So (1) confirm enqueue via
   * last_tx_handle before/after the send and wait THIS frame out by
   * handle for 0x77, (2) same handle-precise wait for the SHUTDOWN
   * broadcast, (3) drain all TX buffers + 3ms insurance delay before
   * NVIC_SystemReset(). */
  have_prev = (can_driver_last_tx_handle(&h_before) == 0) ? 1U : 0U;
  can_proto_send_response(resp, 1);
  if ((can_driver_last_tx_handle(&h_after) == 0) &&
      ((have_prev == 0U) || (h_after != h_before)))
  {
    (void)can_driver_wait_tx_frame(h_after, 50U);
  }

  have_prev = (can_driver_last_tx_handle(&h_bc0) == 0) ? 1U : 0U;
  lifecycle_set_state(LIFECYCLE_SHUTDOWN);
  if ((can_driver_last_tx_handle(&h_after) == 0) &&
      ((have_prev == 0U) || (h_after != h_bc0)))
  {
    (void)can_driver_wait_tx_frame(h_after, 50U);
  }

  /* drain every queued frame off the wire, then a short insurance
   * delay covering TX-complete edge windows (bus transfer tail,
   * transceiver propagation, TSTAT update lag) */
  (void)can_driver_wait_tx_all_idle(50U);
  t0 = timer_get_tick();
  while ((timer_get_tick() - t0) < 3U)
  {
    __NOP();
  }

  NVIC_SystemReset();
}
