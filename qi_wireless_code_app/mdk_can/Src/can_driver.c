/**
  **************************************************************************
  * @file     can_driver.c
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

/* includes ------------------------------------------------------------------*/
#include "can_driver.h"
#include "timer_drv.h"
#include "sit1145.h"
#include <string.h>

/* private define ------------------------------------------------------------*/

/**
 * @brief  Classic CAN 2.0B bit timing (CAN FD unused)
 * @note   AT32F426 CAST CAN-CTRL:
 *         baudrate = can_clk / (div * (bts1 + bts2))
 *         sample   = bts1 / (bts1 + bts2)
 *         BTS1 already includes SYNC_SEG (do not add +1).
 *         CAN kernel = PLL 180 MHz, div = 10 → 18 MHz tq clock
 *         54 + 18 = 72 Tq → 18 MHz / 72 = 250 kbps, SP = 54/72 = 75%
 */
#define CAN_BITTIME_DIV                 10U
#define CAN_BITTIME_SJW                 4U
#define CAN_BITTIME_BTS1                54U
#define CAN_BITTIME_BTS2                18U

/* private variables ---------------------------------------------------------*/

/** @brief  software RX FIFO ring buffer */
static volatile can_rx_frame_t rx_fifo[CAN_DRIVER_RX_FIFO_SIZE];
static volatile uint8_t rx_fifo_head = 0;   /*!< write index (ISR context) */
static volatile uint8_t rx_fifo_tail = 0;   /*!< read index  (main context) */
static volatile uint8_t rx_fifo_count = 0;  /*!< number of pending frames */

/** @brief  RX callback function pointer */
static volatile can_rx_callback_t rx_callback = (can_rx_callback_t)0;

/** @brief  bus-off recovery callback function pointer */
static volatile can_busoff_recovery_callback_t busoff_recovery_cb = (can_busoff_recovery_callback_t)0;

/** @brief  flag set by error ISR when bus-off recovery is needed */
static volatile uint8_t busoff_recovery_pending = 0;

/** @brief  rolling per-frame TX handle, stamped into CAN tx buffer (wraps at 255) */
static uint8_t g_tx_handle_seq = 0U;

/** @brief  handle recorded by the last successful can_driver_send() */
static uint8_t g_tx_last_handle = 0U;

/** @brief  1 after at least one successful can_driver_send() since boot */
static uint8_t g_tx_last_valid = 0U;

static void can_hw_config_in_reset(void);

/* private functions ---------------------------------------------------------*/

/**
 * @brief  check if software RX FIFO is full
 * @retval 1 = full, 0 = not full
 */
static uint8_t rx_fifo_is_full(void)
{
  return (rx_fifo_count >= CAN_DRIVER_RX_FIFO_SIZE) ? 1U : 0U;
}

/* exported functions --------------------------------------------------------*/

/**
 * @brief  initialize CAN1 peripheral in extended frame mode at 250kbps
 * @note   PA11/PA12 AF, SIT1145 Normal, 250 kbps.
 *         Filter 0: 0x18DA0D03; filter 1: 0x18DB33xx. RX and error IRQs.
 * @param  none
 * @retval none
 */
static void can_hw_config_in_reset(void)
{
  can_bittime_type can_bittime_struct;
  can_filter_config_type can_filter_struct;

  /* 正常通信模式（非 Listen-only / 自测） */
  can_mode_set(CAN1, CAN_MODE_COMMUNICATE);

  /* 250 kbps / 75% SP：18 MHz tq / 72 = 250k */
  can_bittime_default_para_init(&can_bittime_struct);
  can_bittime_struct.bittime_div  = CAN_BITTIME_DIV;
  can_bittime_struct.ac_rsaw_size = CAN_BITTIME_SJW;
  can_bittime_struct.ac_bts1_size = CAN_BITTIME_BTS1;
  can_bittime_struct.ac_bts2_size = CAN_BITTIME_BTS2;
  can_bittime_set(CAN1, &can_bittime_struct);

  /* Filter 0：物理 UDS 请求 0x18DA0D03，29 位精确匹配 */
  can_filter_default_para_init(&can_filter_struct);
  can_filter_struct.code_para.id         = CAN_ID_UDS_REQUEST;
  can_filter_struct.code_para.id_type    = CAN_ID_EXTENDED;
  can_filter_struct.code_para.frame_type = CAN_FRAME_DATA;
  can_filter_struct.mask_para.id         = 0x1FFFFFFFU;
  can_filter_struct.mask_para.id_type    = TRUE;
  can_filter_struct.mask_para.frame_type = TRUE;
  can_filter_struct.mask_para.data_length = 0x0FU;
  can_filter_struct.mask_para.recv_frame = TRUE;
  can_filter_set(CAN1, CAN_FILTER_NUM_0, &can_filter_struct);
  can_filter_enable(CAN1, CAN_FILTER_NUM_0, TRUE);

  /* Filter 1：功能寻址 0x18DB33xx（SA 任意），对应 CAN_ID_FUNCTIONAL_REQUEST */
  can_filter_default_para_init(&can_filter_struct);
  can_filter_struct.code_para.id         = 0x18DB3300U;
  can_filter_struct.code_para.id_type    = CAN_ID_EXTENDED;
  can_filter_struct.code_para.frame_type = CAN_FRAME_DATA;
  can_filter_struct.mask_para.id         = 0x1FFFFF00U;
  can_filter_struct.mask_para.id_type    = TRUE;
  can_filter_struct.mask_para.frame_type = TRUE;
  can_filter_struct.mask_para.data_length = 0x0FU;
  can_filter_struct.mask_para.recv_frame = TRUE;
  can_filter_set(CAN1, CAN_FILTER_NUM_1, &can_filter_struct);
  can_filter_enable(CAN1, CAN_FILTER_NUM_1, TRUE);
}


/**
 * @brief  CAN1 上电初始化（250 kbps 扩展帧），完成后保持软件复位
 * @note   只做硬件与软件状态准备，不上总线。
 *         真正退出复位、开中断在 can_driver_online()
 *         （SIT1145 已进 Normal，或生命周期从 Standby 唤醒之后）。
 *         可重复调用：再次 init 等于重新配时钟/GPIO/滤波并清空软件 FIFO。
 */
void can_driver_init(void)
{
  gpio_init_type gpio_init_struct;


  /* ---- 时钟 ----
   * GPIOA：PA11/PA12。
   * CAN1：外设时钟。
   * 内核时钟必须切 PLL（180 MHz）。复位默认 HEXT 8 MHz，
   * 位时序仍按 180 MHz 配的话，实际只有约 11 kbps，对端全是错误帧。 */
  crm_periph_clock_enable(CRM_GPIOA_PERIPH_CLOCK, TRUE);
  crm_periph_clock_enable(CRM_CAN1_PERIPH_CLOCK, TRUE);
  crm_can_clock_select(CRM_CAN1, CRM_CAN_CLOCK_SOURCE_PLL);

  /* ---- PA11 = CAN_RX，复用 AF4，上拉 ----
   * 总线隐性为高，不上拉复位后 RX 可能浮空被读成显性。 */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = GPIO_PINS_11;
  gpio_init_struct.gpio_mode           = GPIO_MODE_MUX;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_UP;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(GPIOA, &gpio_init_struct);
  gpio_pin_mux_config(GPIOA, GPIO_PINS_SOURCE11, GPIO_MUX_4);

  /* ---- PA12 = CAN_TX，复用 AF4，无上下拉 ----
   * TX 由控制器推挽驱动，再加上下拉会和总线显性/隐性抢电平。 */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = GPIO_PINS_12;
  gpio_init_struct.gpio_mode           = GPIO_MODE_MUX;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_NONE;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(GPIOA, &gpio_init_struct);
  gpio_pin_mux_config(GPIOA, GPIO_PINS_SOURCE12, GPIO_MUX_4);

  /* ---- SIT1145：SPI 配寄存器并尽量切到 Normal ----
   * 返回 1=成功、0=失败。失败再试一次（上电 SPI/芯片未就绪）。
   * 第二次仍失败也继续：控制器配置不能卡死启动，
   * online 路径里还会再 sit1145_normal_mode_set()。 */
  if (sit1145_init() == 0U)
  {
    (void)sit1145_init();
  }

  /* ---- 控制器进软件复位后再写模式/位时序/滤波 ----
   * can_reset：CRM 外设复位，CTRLSTAT.RESET=1。
   * can_software_reset(TRUE)：保持复位，配置位此时才可写。
   * can_hw_config_in_reset：250k + Filter0 物理 UDS + Filter1 功能寻址。
   * 这里故意不 can_software_reset(FALSE)：收发器若还在 Standby，
   * 提前出复位会在空总线上堆积错误，甚至进 bus-off。 */
  can_reset(CAN1);
  can_software_reset(CAN1, TRUE);
  can_hw_config_in_reset();
  /* Stay in software reset until SIT1145 leaves Standby (can_driver_online). */

  /* ---- 软件侧状态清零（硬件复位清不掉这些静态量） ---- */
  rx_fifo_head  = 0;  /* ISR 写指针 */
  rx_fifo_tail  = 0;  /* 主循环读指针 */
  rx_fifo_count = 0;
  rx_callback   = (can_rx_callback_t)0;
  busoff_recovery_cb     = (can_busoff_recovery_callback_t)0;
  busoff_recovery_pending = 0;
}


/**
 * @brief  CAN1 下线：关中断并进入软件复位，不再 ACK / 发帧
 * @note   不关时钟、不改 GPIO、不动 SIT1145、不清软件 FIFO。
 *         给生命周期进 Standby 用：必须在收发器切 Standby、引脚改 WUP
 *         监听之前调用，避免 RXD 被拉低时控制器当成显性而 bus-off。
 *         恢复走 can_driver_online()（会再配滤波、出复位、开中断）。
 */
void can_driver_offline(void)
{
  /* 先关 NVIC，避免关外设中断使能时已经 pending 的 IRQ 再进一次 */
  nvic_irq_disable(CAN1_RX_IRQn);
  nvic_irq_disable(CAN1_ERR_IRQn);

  /* 再关控制器内部 RX / 错误中断源 */
  can_interrupt_enable(CAN1, CAN_RIE_INT, FALSE);
  can_interrupt_enable(CAN1, CAN_EIE_INT, FALSE);

  /* 软件复位：停止位时序、不 ACK、配置寄存器保持可写。
   * 不用 can_reset() 全外设复位，避免把刚配好的滤波清掉后还要再配一遍；
   * online 路径在需要时会自己 can_reset + can_hw_config_in_reset。 */
  can_software_reset(CAN1, TRUE);
}


/**
 * @brief  CAN1 上线：重建配置、退出软件复位、打开 RX/ERR 中断
 * @note   前置：can_driver_init() 已配过时钟/GPIO；SIT1145 已 Normal。
 *         从 Standby 唤醒时，必须先等 RXD 结束强制低，再调本函数，
 *         否则一出复位就看到显性，容易直接 bus-off。
 */
void can_driver_online(void)
{
  /* 配置期间禁止一切 CAN 中断，避免半配好的滤波被 ISR 用到 */
  nvic_irq_disable(CAN1_RX_IRQn);
  nvic_irq_disable(CAN1_ERR_IRQn);
  can_interrupt_enable(CAN1, CAN_RIE_INT, FALSE);
  can_interrupt_enable(CAN1, CAN_EIE_INT, FALSE);

  /* 整外设复位 + 再进软件复位，然后重写模式/位时序/滤波。
   * 只靠 software_reset 位，长时间挂着（注释里的「6 分钟」）
   * 后再上线，出现过 RX 中断或滤波器不恢复的情况，所以这里用 CRM
   * can_reset 把外设翻一遍。 */
  can_reset(CAN1);
  can_software_reset(CAN1, TRUE);
  can_hw_config_in_reset();

  /* 清控制器状态，避免复位前挂起的帧/错误一开中断就进 ISR */
  can_flag_clear(CAN1, CAN_RIF_FLAG);   /* RX 完成 */
  can_flag_clear(CAN1, CAN_ROIF_FLAG);  /* RX 溢出 */
  can_flag_clear(CAN1, CAN_EIF_FLAG);   /* 错误汇总 */
  can_flag_clear(CAN1, CAN_BEIF_FLAG);  /* 总线错误 */
  can_flag_clear(CAN1, CAN_ALIF_FLAG);  /* 仲裁丢失 */
  can_flag_clear(CAN1, CAN_EPIF_FLAG);  /* 错误被动 */
  NVIC_ClearPendingIRQ(CAN1_RX_IRQn);
  NVIC_ClearPendingIRQ(CAN1_ERR_IRQn);

  /* 退出软件复位：此时开始采样总线、对有效帧 ACK */
  can_software_reset(CAN1, FALSE);

  /* 先开外设中断源，再开 NVIC。
   * RX 优先级 1，ERR 优先级 2：收帧优先于错误处理。 */
  can_interrupt_enable(CAN1, CAN_RIE_INT, TRUE);
  can_interrupt_enable(CAN1, CAN_EIE_INT, TRUE);
  nvic_irq_enable(CAN1_RX_IRQn, 1, 0);
  nvic_irq_enable(CAN1_ERR_IRQn, 2, 0);
}

/**
 * @brief  CAN1 收发引脚激活：PA11/PA12 重配回 CAN1 复用（MUX_4）
 * @note   调用时机：协议层进入 active 时（can_lp_enter_normal，
 *         can_protocol.c），且须先于 sit1145_normal_mode_set() 与
 *         can_driver_online()（TXD 必须先回 CAN AF 再切 Normal，
 *         否则切收发器瞬间 TXD 仍为 GPIO 态，误打显性会卡总线）。
 *         引脚归属（AT32F422 porta iomux 表 mux4 列，勿记反）：
 *         PA11 = CAN1_RX（SIT1145 RXD -> MCU，输入）、
 *         PA12 = CAN1_TX（MCU -> SIT1145 TXD，输出）。
 *         与 can_driver_pins_standby() 互逆：Standby 下两脚退为 GPIO
 *         （PA12 推高保隐性、PA11 输入听唤醒），唤醒后由本函数复原。 */
void can_driver_pins_active(void)
{
  gpio_init_type gpio_init_struct;

  /* PA11 = CAN1_RX（MUX_4，复用输入）：方向由 CAN 外设接管。
   * 上拉：收发器未上电/悬空时 RXD 无驱动，上拉把引脚钳在高电平
   * （CAN 隐性），避免浮空读成显性而误收帧/报错。STRONGER 对纯输入
   * 脚无实际作用，此处仅与 TX 段写法对称。 */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = GPIO_PINS_11;
  gpio_init_struct.gpio_mode           = GPIO_MODE_MUX;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_UP;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(GPIOA, &gpio_init_struct);
  gpio_pin_mux_config(GPIOA, GPIO_PINS_SOURCE11, GPIO_MUX_4);

  /* PA12 = CAN1_TX（MUX_4，复用推挽输出）：直驱 SIT1145 TXD，
   * 空闲高=隐性（拉低=显性，见 can_driver_pins_standby 注释）。
   * 无上下拉：稳态电平由推挽驱动决定，PULL_NONE 避免切换瞬间
   * 上拉与输出级争电流；STRONGER 加强驱动，保证高速 CAN 边沿
   * 陡峭（250kbps 位时间 4µs，边沿裕量靠驱动强度兜底）。 */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = GPIO_PINS_12;
  gpio_init_struct.gpio_mode           = GPIO_MODE_MUX;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_NONE;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(GPIOA, &gpio_init_struct);
  gpio_pin_mux_config(GPIOA, GPIO_PINS_SOURCE12, GPIO_MUX_4);
}



void can_driver_pins_standby(void)
{
  gpio_init_type gpio_init_struct;

  /* CAN 隐性 = TXD 高。拉低是显性，会把总线卡死并 bus-off。
   * Round-3 (H2): preload ODR=1 BEFORE the pin becomes an output —
   * gpio_init() does not touch ODR (reset value 0), so the old order
   * (gpio_init -> gpio_bits_set) drove PA12/TXD low for the few cycles
   * between the two calls on every entry (dominant glitch on TXD while
   * the transceiver is settling into Standby; bench 1.1.4 phantom-WUP
   * evidence SB+124~141ms ev63=0x01 zero frames). Writing ODR while the
   * pin is still in CAN AF mux is harmless; by the time cfgr switches to
   * GPIO output the latch already reads recessive. */
  gpio_bits_set(GPIOA, GPIO_PINS_12);
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = GPIO_PINS_12;
  gpio_init_struct.gpio_mode           = GPIO_MODE_OUTPUT;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_UP;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_MODERATE;
  gpio_init(GPIOA, &gpio_init_struct);
  gpio_bits_set(GPIOA, GPIO_PINS_12);   /* idempotent re-assert after mode switch */

  /* RXD 作 GPIO 输入，Standby 唤醒时 SIT1145 强制拉低 */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = GPIO_PINS_11;
  gpio_init_struct.gpio_mode           = GPIO_MODE_INPUT;
  gpio_init_struct.gpio_pull           = GPIO_PULL_UP;
  gpio_init(GPIOA, &gpio_init_struct);
}

/**
 * @brief  try to enqueue and trigger one frame in a specific TX buffer
 * @note   发送邮箱准入/写入/触发的最小单元（FC 发送竞态修复）。准入判据
 *         与库内部一致（at32f422_426_can.c can_txbuf_write）：PTB 在
 *         ctrlstat.tpe==TRUE（写锁未清）时拒写、STB 在
 *         ctrlstat.tsnext==TRUE 时拒写，STB 另有 FIFO 满预检。任一步被
 *         拒即 -1 且不留半成品（can_txbuf_write 在触碰缓冲 RAM 前就
 *         会因写锁返回 ERROR）。旧 can_driver_send 对该 ERROR 直接 -1
 *         不换另一邮箱——「tstat 说空、写锁还锁着」窗口内整帧静默丢失
 *         （实机 30 次压测 3 次 FF 后无 FC 的根因层 2），故由
 *         can_driver_send 在本函数返回 -1 时回退另一邮箱再试一次。
 * @param  sel:    CAN_TXBUF_PTB / CAN_TXBUF_STB
 * @param  tx_buf: frame descriptor (id/dlc/data/handle already filled)
 * @retval 0 = enqueued and triggered, -1 = rejected (locked/full/write error)
 */
static int8_t can_try_txbuf(can_txbuf_select_type sel, can_txbuf_type *tx_buf)
{
  if (sel == CAN_TXBUF_STB)
  {
    if (can_stb_status_get(CAN1) == CAN_STB_STATUS_FULL)
    {
      return -1;
    }
  }

  if (can_txbuf_write(CAN1, sel, tx_buf) != SUCCESS)
  {
    return -1;
  }

  if (sel == CAN_TXBUF_PTB)
  {
    can_txbuf_transmit(CAN1, CAN_TRANSMIT_PTB);
  }
  else
  {
    can_txbuf_transmit(CAN1, CAN_TRANSMIT_STB_ONE);
  }
  return 0;
}

/**
 * @brief  transmit a CAN extended frame
 * @note   NOT thread-safe - call only from main loop context.
 *         Do not call from ISR or multiple threads without external locking.
 *         每次成功入队为该帧盖一个递增 handle（uint8 循环回绕）并记录：
 *         AT32 CAST CAN-CTRL 无 bxCAN 式 mailbox TME 标志，库通过
 *         tbtyp.handle + TSTAT（handle_1/tstat_1 当前帧、handle_2/tstat_2
 *         最近完成帧）提供「指定帧发送完成」的判定通道，供
 *         can_driver_wait_tx_frame() 按 handle 精确等待某一帧，避免笼统
 *         TX-idle 判定在多缓冲（PTB+STB FIFO）场景下误把其他帧/空缓冲
 *         的状态当成目标帧完成（ECUReset 51 01 偶发丢失的竞态根因）。
 *         双邮箱回退（FC 发送竞态修复）：首选邮箱被拒（STB 满/
 *         can_txbuf_write 写锁 ERROR）时换另一邮箱再试一次，两个都失败
 *         才 -1，详见 can_try_txbuf 注释。返回值语义保持 0/-1 不变，
 *         现有调用方不受影响。
 * @param  id:   29-bit extended identifier
 * @param  data: pointer to transmit data buffer
 * @param  len:  data length (0~8)
 * @retval 0 on success, -1 on failure (both TX buffers rejected or invalid parameter)
 */
int8_t can_driver_send(uint32_t id, uint8_t *data, uint8_t len)
{
  can_txbuf_type tx_buf;
  can_txbuf_select_type txbuf_sel;
  can_transmit_status_type tx_status;
  uint8_t i;

  /* validate parameters */
  if ((data == (uint8_t *)0) || (len > CAN_DRIVER_MAX_DATA_LEN))
  {
    return -1;
  }

  /* CAST CAN-CTRL has SUPPORT_CAN_FD. Uninitialized fd_format/brs on the
   * stack sets FDF=1 and the host traces MCU replies as CAN FD. */
  memset(&tx_buf, 0, sizeof(tx_buf));
  tx_buf.id             = id;
  tx_buf.id_type        = CAN_ID_EXTENDED;
  tx_buf.frame_type     = CAN_FRAME_DATA;
  tx_buf.fd_format      = CAN_FORMAT_CLASSIC;
  tx_buf.fd_rate_switch = CAN_BRS_OFF;
  tx_buf.handle         = g_tx_handle_seq;  /* 帧标识 handle，见函数注释 */
  tx_buf.tx_timestamp   = FALSE;

  /* set data length code */
  switch (len)
  {
    case 0: tx_buf.data_length = CAN_DLC_BYTES_0; break;
    case 1: tx_buf.data_length = CAN_DLC_BYTES_1; break;
    case 2: tx_buf.data_length = CAN_DLC_BYTES_2; break;
    case 3: tx_buf.data_length = CAN_DLC_BYTES_3; break;
    case 4: tx_buf.data_length = CAN_DLC_BYTES_4; break;
    case 5: tx_buf.data_length = CAN_DLC_BYTES_5; break;
    case 6: tx_buf.data_length = CAN_DLC_BYTES_6; break;
    case 7: tx_buf.data_length = CAN_DLC_BYTES_7; break;
    default: tx_buf.data_length = CAN_DLC_BYTES_8; break;
  }

  /* copy data */
  for (i = 0; i < len; i++)
  {
    tx_buf.data[i] = data[i];
  }

  /* mailbox pick unchanged: try primary transmit buffer (PTB) first for
   * higher priority while tstat says the current slot is free/done
   * (TRANSMITTED(3) also means the slot is reusable); otherwise STB first.
   * tstat only biases the try order — actual admission lives in
   * can_try_txbuf (STB-full pre-check + can_txbuf_write), whose rejection
   * now falls back to the other mailbox instead of failing outright. */
  can_transmit_status_get(CAN1, &tx_status);
  if ((tx_status.current_tstat == CAN_TSTAT_IDLE) ||
      (tx_status.current_tstat == CAN_TSTAT_TRANSMITTED))
  {
    txbuf_sel = CAN_TXBUF_PTB;
  }
  else
  {
    txbuf_sel = CAN_TXBUF_STB;
  }

  /* dual-mailbox fallback (FC TX race fix): the old code returned -1 the
   * moment can_txbuf_write() rejected the pick, never trying the other
   * buffer (PTB write-locked => never tried STB; STB blocked/full => never
   * tried PTB). Inside the "tstat says free but the ctrlstat write lock
   * (tpe/tsnext, see at32f422_426_can.c can_txbuf_write) is still set"
   * window a critical frame such as the ISO-TP FC vanished silently —
   * root cause of 3/30 stress runs with no FC after RequestDownload FF.
   * Try the other mailbox once on rejection; -1 only if both reject. */
  if (can_try_txbuf(txbuf_sel, &tx_buf) != 0)
  {
    txbuf_sel = (txbuf_sel == CAN_TXBUF_PTB) ? CAN_TXBUF_STB : CAN_TXBUF_PTB;
    if (can_try_txbuf(txbuf_sel, &tx_buf) != 0)
    {
      return -1;
    }
  }

  /* 记录本帧 handle 并推进序号（仅在真正入队成功后更新，供调用方
   * 用发送前后 handle 对比识别静默入队失败）。uint8 自然回绕：等待方
   * 在发送后立即按 handle 等待，一次等待窗内不可能跨越 256 次成功
   * 发送，回绕不构成误判 */
  g_tx_last_handle = g_tx_handle_seq;
  g_tx_last_valid  = 1U;
  g_tx_handle_seq++;

  return 0;
}

/**
 * @brief  register a callback for received CAN frames
 * @note   the callback is invoked from can_driver_poll() in main loop context
 * @param  cb: callback function pointer, or NULL to unregister
 * @retval none
 */
void can_driver_register_rx_callback(can_rx_callback_t cb)
{
  rx_callback = cb;
}


/**
 * @brief  从软件 RX FIFO 取出最老的一帧
 * @param  id   输出 29 位扩展 ID
 * @param  data 输出数据，调用方保证至少 8 字节
 * @param  len  输出本帧数据长度 0~8
 * @retval  0  取到一帧
 *         -1  空队列或参数空指针
 * @note   关中断拷贝再改 tail/count，避免与 RX ISR 抢同一格。
 *         不读硬件邮箱；硬件 → FIFO 在 can_driver_rx_irq_handler()。
 *         主循环可连续调，直到返回 -1。
 */
int8_t can_driver_recv(uint32_t *id, uint8_t *data, uint8_t *len)
{
  uint8_t i;
  uint8_t n;

  if ((id == (uint32_t *)0) || (data == (uint8_t *)0) || (len == (uint8_t *)0))
  {
    return -1;
  }

  /* ISR 会改 head/count，读-改-写必须原子 */
  __disable_irq();
  if (rx_fifo_count == 0U)
  {
    __enable_irq();
    return -1;
  }

  *id  = rx_fifo[rx_fifo_tail].id;
  n    = rx_fifo[rx_fifo_tail].len;
  *len = n;
  for (i = 0; i < n; i++)
  {
    data[i] = rx_fifo[rx_fifo_tail].data[i];
  }
  rx_fifo_tail = (rx_fifo_tail + 1) % CAN_DRIVER_RX_FIFO_SIZE;
  rx_fifo_count--;
  __enable_irq();
  return 0;
}


/**
 * @brief  等待当前 TX 槽空闲或已发完，最多 timeout_ms
 * @param  timeout_ms  超时（与 timer_get_tick() 同一单位，本工程是 ms）
 * @retval  0  current_tstat 已是 IDLE 或 TRANSMITTED
 *         -1  超时；不区分「还在发」还是「被中止/拒绝」
 *
 * @note   只看 current_tstat，不看：
 *         - 这是不是刚刚 send 的那一帧（不核对 handle）
 *         - STB 三格 FIFO 里还有没有排队（不调 can_stb_status_get）
 *
 *         因此：PTB 刚发完、STB 里还塞着下一帧时也会返回 0。
 *         多邮箱场景下「别的帧/空槽的状态」就能把条件满足。
 *
 *         适用：Boot M1~M4、普通尽力排空（超时就丢，不挡主流程）。
 *         不适用：复位前确认 0x77 / UDS 应答已上总线
 *         → can_driver_wait_tx_frame(handle)
 *         → 再 can_driver_wait_tx_all_idle()（额外要求 STB 为空）
 *
 *         忙等，不 sleep、不喂看门狗；timeout 过大要考虑调用上下文。
 */
int8_t can_driver_wait_tx_idle(uint32_t timeout_ms)
{
  uint32_t start = timer_get_tick();
  can_transmit_status_type tx_status;

  do
  {
    can_transmit_status_get(CAN1, &tx_status);
    if ((tx_status.current_tstat == CAN_TSTAT_IDLE) ||
        (tx_status.current_tstat == CAN_TSTAT_TRANSMITTED))
    {
      return 0;
    }
  } while ((timer_get_tick() - start) < timeout_ms);

  return -1;
}

/**
 * @brief  get the handle stamped by the last successful can_driver_send()
 * @note   调用方在发送前后各取一次 handle：若未推进，说明该帧因缓冲
 *         满/参数错未真正入队（can_driver_send 返回 -1 路径），据此可
 *         区分「发送失败」与「已入队但尚未发出」，避免对陈旧 handle
 *         空等得到假完成。
 * @param  handle: out, handle of the last successful send
 * @retval 0 on success, -1 if no frame has been sent since boot
 */
int8_t can_driver_last_tx_handle(uint8_t *handle)
{
  if ((handle == (uint8_t *)0) || (g_tx_last_valid == 0U))
  {
    return -1;
  }
  *handle = g_tx_last_handle;
  return 0;
}

/**
 * @brief  wait until the frame identified by handle has been transmitted
 * @note   判定依据（AT32F426 CAST CAN-CTRL）：
 *         本控制器没有 bxCAN 式 mailbox TME 标志，固件库
 *         at32f422_426_can.c:842 can_transmit_status_get() 实读 tstat
 *         寄存器两组字段——current_handle/tstat_1（tstat_bit.handle_1
 *         [7:0] / tstat_1 [10:8]）为当前处理帧的标识与状态，
 *         final_handle/tstat_2（handle_2 [23:16] / tstat_2 [26:24]）为
 *         最近完成帧的标识与状态；库在 can_txbuf_write() 中把
 *         tx_buf.handle 写入 tbtyp_bit.handle（at32f422_426_can.c:422），
 *         header 对 handle 的定义即「frame identification using TSTAT」。
 *         因此「指定帧已发送完成」的可靠判定 = 「TSTAT 任一组 handle
 *         匹配且状态为 CAN_TSTAT_TRANSMITTED(0x03)」。
 *         状态语义（at32f422_426_can.h:366-372）：TRANSMITTED=0x03
 *         发送成功；LOST_ARBITRATION(0x02)/DISTURBED(0x05) 由硬件按
 *         REALIM/RETLIM 自动重发，不算终态，继续轮询；
 *         ABORTED(0x04)/REJECTED(0x06) 为终态失败，帧不会再上总线，
 *         立即返回 -1 让上层走补发，不空等超时。
 *         顺序安全性：调用方在本函数返回前不发起新发送，
 *         final 槽不会被后续帧覆盖；其他帧的状态（含他帧
 *         TRANSMITTED）一律不作为本帧完成证据——这正是旧 TX-idle 笼统
 *         判定的竞态根因，此处按 handle 隔离。
 * @param  handle: frame handle recorded at enqueue time
 *         (can_driver_last_tx_handle)
 * @param  timeout_ms: maximum wait in ms
 * @retval 0 = frame transmitted OK, -1 = timeout or terminal failure
 */
int8_t can_driver_wait_tx_frame(uint8_t handle, uint32_t timeout_ms)
{
  uint32_t start = timer_get_tick();
  can_transmit_status_type tx_status;

  do
  {
    can_transmit_status_get(CAN1, &tx_status);

    /* 指定帧完成的正证据：任一组 handle 匹配且状态 TRANSMITTED */
    if (((tx_status.final_handle == handle) &&
         (tx_status.final_tstat == CAN_TSTAT_TRANSMITTED)) ||
        ((tx_status.current_handle == handle) &&
         (tx_status.current_tstat == CAN_TSTAT_TRANSMITTED)))
    {
      return 0;
    }

    /* 本帧终态失败：不会再上总线，立即交给上层补发 */
    if ((tx_status.final_handle == handle) &&
        ((tx_status.final_tstat == CAN_TSTAT_ABORTED) ||
         (tx_status.final_tstat == CAN_TSTAT_REJECTED)))
    {
      return -1;
    }

    /* 其他帧/其他 handle 的状态一律不作为本帧完成证据 */
  } while ((timer_get_tick() - start) < timeout_ms);

  return -1;
}

/**
 * @brief  wait until all TX buffers are idle (fallback completion check)
 * @note   退路判定：本控制器（CAST CAN-CTRL）没有 bxCAN 式「全部
 *         mailbox TME 置位」接口，等价组合为：PTB 空闲/已发完
 *         （current_tstat ∈ {CAN_TSTAT_IDLE(0x00),
 *         CAN_TSTAT_TRANSMITTED(0x03)}）且 STB FIFO 排空
 *         （can_stb_status_get == CAN_STB_STATUS_EMPTY(0x00)）。
 *         仅作按 handle 精确判定之后、复位前的兜底确认（所有已入队
 *         帧均已离开发送缓冲），不替代 can_driver_wait_tx_frame。
 * @param  timeout_ms: maximum wait in ms
 * @retval 0 = all TX resources idle, -1 = timeout
 */
int8_t can_driver_wait_tx_all_idle(uint32_t timeout_ms)
{
  uint32_t start = timer_get_tick();
  can_transmit_status_type tx_status;

  do
  {
    can_transmit_status_get(CAN1, &tx_status);
    if (((tx_status.current_tstat == CAN_TSTAT_IDLE) ||
         (tx_status.current_tstat == CAN_TSTAT_TRANSMITTED)) &&
        (can_stb_status_get(CAN1) == CAN_STB_STATUS_EMPTY))
    {
      return 0;
    }
  } while ((timer_get_tick() - start) < timeout_ms);

  return -1;
}

/**
 * @brief  process received CAN frames from software FIFO
 * @note   must be called periodically from main loop.
 *         invokes registered callback for each pending frame.
 * @param  none
 * @retval none
 */
void can_driver_poll(void)
{
  can_rx_frame_t frame;

  while (rx_fifo_count > 0)
  {
    /* copy frame from FIFO (atomic read of volatile struct) */
    frame.id  = rx_fifo[rx_fifo_tail].id;
    frame.len = rx_fifo[rx_fifo_tail].len;
    {
      uint8_t i;
      for (i = 0; i < frame.len; i++)
      {
        frame.data[i] = rx_fifo[rx_fifo_tail].data[i];
      }
    }

    /* advance tail pointer */
    rx_fifo_tail = (rx_fifo_tail + 1) % CAN_DRIVER_RX_FIFO_SIZE;

    /* decrement count (main context only reader, safe without lock) */
    __disable_irq();
    rx_fifo_count--;
    __enable_irq();

    /* invoke callback */
    if (rx_callback != (can_rx_callback_t)0)
    {
      rx_callback(frame.id, frame.data, frame.len);
    }
  }

  /* handle pending bus-off recovery notification (deferred from ISR) */
  if (busoff_recovery_pending)
  {
    busoff_recovery_pending = 0;
    if (busoff_recovery_cb != (can_busoff_recovery_callback_t)0)
    {
      busoff_recovery_cb();
    }
  }
}

/**
 * @brief  CAN1 RX interrupt handler (called from CAN1_RX_IRQHandler)
 * @note   reads frame from hardware RX buffer into software FIFO,
 *         clears interrupt flag.
 * @param  none
 * @retval none
 */
void can_driver_rx_irq_handler(void)
{
  can_rxbuf_type rx_buf;
  uint8_t i;

  /* check RX interrupt flag */
  if (can_flag_get(CAN1, CAN_RIF_FLAG) != RESET)
  {
    /* read frame from hardware RX buffer */
    if (can_rxbuf_read(CAN1, &rx_buf) == SUCCESS)
    {
      /* store in software FIFO if not full */
      if (!rx_fifo_is_full())
      {
        rx_fifo[rx_fifo_head].id  = rx_buf.id;
        rx_fifo[rx_fifo_head].len = (uint8_t)(rx_buf.data_length & 0x0F);
        if (rx_fifo[rx_fifo_head].len > CAN_DRIVER_MAX_DATA_LEN)
        {
          rx_fifo[rx_fifo_head].len = CAN_DRIVER_MAX_DATA_LEN;
        }
        for (i = 0; i < rx_fifo[rx_fifo_head].len; i++)
        {
          rx_fifo[rx_fifo_head].data[i] = rx_buf.data[i];
        }
        rx_fifo_head = (rx_fifo_head + 1) % CAN_DRIVER_RX_FIFO_SIZE;
        rx_fifo_count++;
      }
    }

    /* release RX buffer */
    can_rxbuf_release(CAN1);

    /* clear RX interrupt flag */
    can_flag_clear(CAN1, CAN_RIF_FLAG);
  }

  /* handle RX overflow */
  if (can_flag_get(CAN1, CAN_ROIF_FLAG) != RESET)
  {
    can_flag_clear(CAN1, CAN_ROIF_FLAG);
  }
}

/**
 * @brief  CAN1 error interrupt handler (called from CAN1_ERR_IRQHandler)
 * @note   clears error flags and performs error recovery if bus-off.
 * @param  none
 * @retval none
 */
void can_driver_err_irq_handler(void)
{
  /* check error warning flag */
  if (can_flag_get(CAN1, CAN_EIF_FLAG) != RESET)
  {
    /* check for bus-off condition */
    if (can_busoff_get(CAN1) != RESET)
    {
      /* recover from bus-off by requesting recovery */
      can_busoff_reset(CAN1);

      /* set flag for deferred notification in can_driver_poll() */
      busoff_recovery_pending = 1;
    }

    /* clear error interrupt flag */
    can_flag_clear(CAN1, CAN_EIF_FLAG);
  }

  /* clear bus error flag if set */
  if (can_flag_get(CAN1, CAN_BEIF_FLAG) != RESET)
  {
    can_flag_clear(CAN1, CAN_BEIF_FLAG);
  }

  /* clear arbitration lost flag if set */
  if (can_flag_get(CAN1, CAN_ALIF_FLAG) != RESET)
  {
    can_flag_clear(CAN1, CAN_ALIF_FLAG);
  }

  /* clear error passive flag if set */
  if (can_flag_get(CAN1, CAN_EPIF_FLAG) != RESET)
  {
    can_flag_clear(CAN1, CAN_EPIF_FLAG);
  }
}

/**
 * @brief  register a callback for bus-off recovery event
 * @note   the callback is invoked from can_driver_poll() when bus-off
 *         recovery is detected. keeps ISR context minimal.
 * @param  cb: callback function pointer, or NULL to unregister
 * @retval none
 */
void can_driver_register_busoff_recovery_callback(can_busoff_recovery_callback_t cb)
{
  busoff_recovery_cb = cb;
}
