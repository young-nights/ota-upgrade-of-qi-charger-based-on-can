/**
  **************************************************************************
  * @file     boot_safe_mode.c
  * @brief    Safe mode: no bootable slot. Persist failure context and
  *           answer minimal CAN diagnostic probe frames.
  **************************************************************************
  *
  * Field OTA download lives in APP. Boot only selects a slot and jumps.
  * Empty chip / both slots invalid: stay here and report why via CAN.
  * Factory image: merge_prod_bin.py from 0x08000000.
  *
  * ---- safe mode 诊断帧格式（与 python_tools/zcanpro_ext_ota_auto.py
  * ---- 头部 SAFE_MODE 注释严格一致，两侧勿改其一）----
  *
  *   探测请求: CAN ID 0x18DA0D03 (CAN_ID_UDS_REQUEST)
  *             数据 22 21 13 —— RX 为自实现兼容解析（不依赖 ISO-TP
  *             协议栈，见下方 enter_safe_mode 接收循环），兼容 ISO-TP SF
  *             (03 22 21 13 ...) 与裸 UDS (22 21 13 ...) 两种写法
  *   22 2113 应答: CAN ID 0x18DA030D (CAN_ID_UDS_RESPONSE)
  *             ISO-TP 单帧，DLC=8（safe_send_sf 组帧，尾部 0xCC 填充）:
  *
  *             05 62 21 13 FE <fail_step> CC CC
  *
  *             PCI=0x05 表示单帧载荷 5 字节。主机侧解析在
  *             python_tools/zcanpro_ext_ota_auto.py _safe_mode_step，
  *             双格式兼容：历史裸帧 62 21 13 FE <fail_step> + 现行
  *             ISO-TP SF 05 62 21 13 FE <fail_step>
  *   3E 应答: suppress 位（sub bit7）为 0 时回 ISO-TP 单帧
  *             02 7E <子功能低 7 位>（同 0x18DA030D）；suppress 位
  *             为 1 不应答
  *   其他 22 DID: 回 NRC ISO-TP 单帧 7F 22 11（servicesNotSupported）
  *   心跳: 每 500ms 在 CAN ID 0x18FF260D (CAN_ID_LIFECYCLE_BROADCAST)
  *             发 01 41 42 54 cause fail_step A5 00（'ABT' 标记帧），
  *             并同周期重切 SIT1145 收发器 Normal
  *             （sit1145_normal_mode_set，见 enter_safe_mode 主循环）
  *
  *   fail_step 语义（提取自 boot_verify.c g_verify_fail_step）:
  *     0 = image verify not executed / no usable metadata state
  *     1 = 镜像 magic 校验失败
  *     2 = image_length 为 0 或超出槽范围
  *     3 = 镜像 CRC32 校验失败
  *     4 = Reset handler 不在槽内（跨槽链接镜像）
  *     5 = 已废弃（ECDSA 验签已移除，2026-09-24）不再产生
  *     6 = 已废弃（ECDSA 验签已移除，2026-09-24）不再产生
  *
  **************************************************************************
  */

#include "boot_safe_mode.h"
#include "boot_metadata.h"
#include "boot_trial.h"
#include "boot_verify.h"
#include "can_driver.h"
#include "sit1145.h"
#include "timer_drv.h"
#include "at32f422_426.h"
#include "at32f422_426_can.h"

static void safe_send_sf(const uint8_t *uds, uint8_t n)
{
  uint8_t sf[8];
  uint8_t i;

  if ((uds == (const uint8_t *)0) || (n == 0U) || (n > 7U))
  {
    return;
  }
  sf[0] = n;
  for (i = 0U; i < n; i++)
  {
    sf[1U + i] = uds[i];
  }
  for (i = (uint8_t)(1U + n); i < 8U; i++)
  {
    sf[i] = 0xCCU;
  }
  (void)can_driver_send(CAN_ID_UDS_RESPONSE, sf, 8U);
}

static void safe_heartbeat(uint8_t cause, uint8_t step)
{
  uint8_t d[8];

  d[0] = 0x01U;
  d[1] = 0x41U;
  d[2] = 0x42U; /* 'B' */
  d[3] = 0x54U; /* 'T' */
  d[4] = cause;
  d[5] = step;
  d[6] = 0xA5U;
  d[7] = 0x00U;
  (void)can_driver_send(CAN_ID_LIFECYCLE_BROADCAST, d, 8U);
}

#define BOOT_DIAG_TX_TIMEOUT_MS  10U
#define BOOT_DIAG_TX_TRIES       3U
#define BOOT_DIAG_SETTLE_MS      5U
uint8_t g_diag_meta_src = 0xFFU;

/**
 * @brief  等到本帧真正离开发送邮箱
 * @note   can_driver_wait_tx_idle 把 TSTAT=IDLE 当完成。刚 trigger 时
 *         硬件还没开发，TSTAT 仍是 IDLE，立刻返回后下一帧会盖 PTB；
 *         M4 随后 can_reset 会把还在邮箱里的帧清掉。至少等过 1ms
 *         （一帧约 0.6ms），再要求 PTB 空闲且 STB 为空。
 */
static int8_t boot_diag_wait_tx_done(void)
{
  uint32_t start = timer_get_tick();
  can_transmit_status_type st;

  do
  {
    can_transmit_status_get(CAN1, &st);
    if ((st.current_tstat == CAN_TSTAT_ABORTED) ||
        (st.current_tstat == CAN_TSTAT_REJECTED))
    {
      return -1;
    }
    if (((timer_get_tick() - start) >= 1U) &&
        ((st.current_tstat == CAN_TSTAT_IDLE) ||
         (st.current_tstat == CAN_TSTAT_TRANSMITTED)) &&
        (can_stb_status_get(CAN1) == CAN_STB_STATUS_EMPTY))
    {
      return 0;
    }
  } while ((timer_get_tick() - start) < BOOT_DIAG_TX_TIMEOUT_MS);

  return -1;
}

/**
 * @brief  发送一帧 Boot 诊断标记（CAN ID 0x18FF480D，扩展帧，DLC=8）
 * @param  payload  至少 1 字节：payload[0] 为标记 0xA1~0xA4
 * @param  n        有效字节数，范围 1~7（第 8 字节起用 0xCC 填充）
 * @note   只报信。发送失败或超时弃帧，不写 metadata、不改启动路径。
 */
static void boot_diag_frame_send(const uint8_t *payload, uint8_t n)
{
  uint8_t f[8];
  uint8_t i;
  uint8_t tries;

  /* 标记占 1 字节，CAN 数据场最多 8，故有效载荷不能超过 7 */
  if ((payload == (const uint8_t *)0) || (n == 0U) || (n > 7U))
  {
    return;
  }
  /* 标记字节原样放入 */
  f[0] = payload[0];
  for (i = 1U; i < 8U; i++)
  {
    /* 有数据就拷，没有就填 0xCC，保证 DLC 恒为 8、抓包对齐 */
    f[i] = (uint8_t)((i < n) ? payload[i] : 0xCCU);
  }

  for (tries = 0U; tries < BOOT_DIAG_TX_TRIES; tries++)
  {
    if (can_driver_send(BOOT_DIAG_CAN_ID, f, 8U) == 0)
    {
      if (boot_diag_wait_tx_done() == 0)
      {
        return;
      }
    }
  }
}

static void boot_diag_delay_ms(uint32_t ms)
{
  uint32_t start = timer_get_tick();

  while ((timer_get_tick() - start) < ms)
  {
  }
}

/**
 * @brief  诊断 CAN 提前初始化（main step1：timer 之后、metadata 之前）
 * @note   目的：让 boot 决策链上的 M1~M4 标记帧（CAN ID 0x18FF480D）
 *         在读 metadata 之前就能发出。
 *         只动 CAN1 / GPIOA / SPI1 / SIT1145 Normal，不碰 Flash、CRC、
 *         metadata、选槽逻辑。失败也不阻断启动。
 *         与 enter_safe_mode() 内初始化相同；那边再调一次是幂等复位。
 */
void boot_diag_can_init(void)
{
  uint8_t i;

  /* MCU CAN1：时钟、GPIO、位时序 250kbps、滤波；内部会 sit1145_init() */
  can_driver_init();

  /* 冷启动 SPI/收发器可能一次没就绪；Normal 才能把帧打到 CANH/CANL */
  for (i = 0U; i < 5U; i++)
  {
    if (sit1145_get_mode() == SIT1145_MC_NORMAL_MODE)
    {
      break;
    }
    (void)sit1145_init();
    (void)sit1145_normal_mode_set();
  }

  /* 控制器刚出复位需要隐性总线；也给对端适配器一点 ACK 准备时间 */
  boot_diag_delay_ms(BOOT_DIAG_SETTLE_MS);
}



/**
 * @brief  发 M1：报告 metadata 来源与结构是否完整
 * @param  meta  应为 ota_metadata_t*（boot_metadata_init 里的 RAM 副本）
 * @note   必须先写 g_diag_meta_src 再调用。
 *
 * 载荷：
 *   [0] 0xA1
 *   [1] meta_src：0 主区 / 1 备区恢复 / 2 默认值 / 0xFF 未记录
 *   [2] magic == "MATO" ? 1 : 0
 *   [3] version == META_VERSION ? 1 : 0
 *   [4] CRC32（不含末尾 4 字节）与 meta->crc32 一致 ? 1 : 0
 */
void boot_diag_m1(const void *meta)
{
  const ota_metadata_t *m = (const ota_metadata_t *)meta;
  uint8_t p[5];

  p[0] = 0xA1U;
  p[1] = g_diag_meta_src;
  if (m != (const ota_metadata_t *)0)
  {
    p[2] = (uint8_t)((m->magic == META_MAGIC) ? 1U : 0U);
    p[3] = (uint8_t)((m->version == META_VERSION) ? 1U : 0U);
    p[4] = (uint8_t)((boot_crc32((const void *)m,
                                 sizeof(ota_metadata_t) - 4U) == m->crc32) ? 1U : 0U);
  }
  else
  {
    p[2] = 0U;
    p[3] = 0U;
    p[4] = 0U;
  }
  boot_diag_frame_send(p, 5U);
}


/**
 * @brief  上报 Boot 诊断帧 M2（单 APP 架构下的 copy 结果）
 *
 * 组 3 字节诊断载荷并发出，字节序与文档、函数参数一致
 *（OTA-ARCH-0920-D5：b1=ret，b2=info）。
 *
 * @param ret   b1，copy 结果
 *              - 0    : 序列开始 / 无 pending（当 b2=0xFF）
 *              - 1    : copy 已提交
 *              - 0xFF : copy 失败
 * @param info  b2，细节
 *              - 失败时为 fail_step
 *              - 0xFF 表示 not pending
 *
 * @note  帧格式：p[0]=0xA2，p[1]=ret，p[2]=info。
 *        与 M1/M3/M4 同一套「参数顺序 = 线上顺序」。
 */
void boot_diag_m2(int8_t ret, uint8_t info)
{
  uint8_t p[3];

  p[0] = 0xA2U;
  p[1] = (uint8_t)ret; /* b1: copy result */
  p[2] = info;         /* b2: detail */
  boot_diag_frame_send(p, 3U);
}

void boot_diag_m3(uint8_t pass, uint8_t fail_step, uint8_t target)
{
  /* M3 payload (single-App arch): b1=pass b2=fail_step
   * (0xFF=verify starting) b3=target region (0=Backup source,
   * 1=App region) */
  uint8_t p[4];

  p[0] = 0xA3U;
  p[1] = pass;
  p[2] = fail_step;
  p[3] = target;
  boot_diag_frame_send(p, 4U);
}

void boot_diag_m4(uint32_t app_addr)
{
  uint8_t p[5];

  p[0] = 0xA4U;
  p[1] = (uint8_t)(app_addr & 0xFFU);
  p[2] = (uint8_t)((app_addr >> 8) & 0xFFU);
  p[3] = (uint8_t)((app_addr >> 16) & 0xFFU);
  p[4] = (uint8_t)((app_addr >> 24) & 0xFFU);
  boot_diag_frame_send(p, 5U);
}

void enter_safe_mode(uint8_t cause)
{
  uint32_t id;
  uint8_t  data[CAN_DRIVER_MAX_DATA_LEN];
  uint8_t  len;
  uint8_t  step = g_verify_fail_step;
  uint32_t last_hb;

  /* Q2 metadata slimming (v3): write-only forensic slots removed.
   * cause/step stay observable live via CAN frames (M2/M3 markers,
   * ABT heartbeat, safe-mode probe response). copy_fail_step written by
   * boot_copy_backup persists through this save. */
  (void)boot_metadata_save(&g_meta);

  can_driver_init();
  (void)sit1145_normal_mode_set();
  last_hb = timer_get_tick();
  safe_heartbeat(cause, step);

  while (1)
  {
    if (can_flag_get(CAN1, CAN_RIF_FLAG) != RESET)
    {
      can_driver_rx_irq_handler();
    }

    while (can_driver_recv(&id, data, &len) == 0)
    {
      uint8_t *u = data;
      uint8_t  un = len;

      if (id != CAN_ID_UDS_REQUEST)
      {
        continue;
      }
      if ((len >= 2U) && ((data[0] & 0xF0U) == 0x00U))
      {
        uint8_t pci_n = data[0] & 0x0FU;
        if ((pci_n >= 1U) && ((uint8_t)(1U + pci_n) <= len))
        {
          u = &data[1];
          un = pci_n;
        }
      }

      if ((un >= 2U) && (u[0] == 0x3EU))
      {
        uint8_t r[2];
        r[0] = 0x7EU;
        r[1] = (uint8_t)(u[1] & 0x7FU);
        if ((u[1] & 0x80U) == 0U)
        {
          safe_send_sf(r, 2U);
        }
      }
      else if ((un >= 3U) && (u[0] == 0x22U) && (u[1] == 0x21U) && (u[2] == 0x13U))
      {
        uint8_t r[5];
        r[0] = 0x62U;
        r[1] = 0x21U;
        r[2] = 0x13U;
        r[3] = 0xFEU;
        r[4] = step;
        safe_send_sf(r, 5U);
      }
      else if ((un >= 1U) && (u[0] == 0x22U))
      {
        uint8_t r[3];
        r[0] = 0x7FU;
        r[1] = 0x22U;
        r[2] = 0x11U;
        safe_send_sf(r, 3U);
      }
    }

    if ((timer_get_tick() - last_hb) >= 500U)
    {
      last_hb = timer_get_tick();
      (void)sit1145_normal_mode_set();
      safe_heartbeat(cause, step);
    }
  }
}
