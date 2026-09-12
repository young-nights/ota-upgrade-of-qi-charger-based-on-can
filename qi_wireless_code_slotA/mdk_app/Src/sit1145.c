/**
  **************************************************************************
  * @file     sit1145.c
  * @brief    SIT1145 CAN FD 收发器驱动（AT32F426）
  *
  * @details  通过 SPI1 与 SIT1145 收发器通信，管理其工作模式切换
  *           （Normal / Standby / Sleep）和 CAN 唤醒检测。
  *
  *           硬件接线：
  *           ┌──────────┬──────────┬──────────────────────┐
  *           │ 引脚     │ 功能     │ 说明                 │
  *           ├──────────┼──────────┼──────────────────────┤
  *           │ PA4      │ SPI1_CS  │ GPIO 软件控制，低有效│
  *           │ PA5      │ SPI1_SCK │ AF MUX0              │
  *           │ PA6      │ SPI1_MISO│ AF MUX0，带上拉      │
  *           │ PA7      │ SPI1_MOSI│ AF MUX0              │
  *           │ PA11     │ CAN_RX   │ 由 can_driver 管理   │
  *           │ PA12     │ CAN_TX   │ 由 can_driver 管理   │
  *           │ PB3      │ 悬空     │ 输出低防悬空         │
  *           └──────────┴──────────┴──────────────────────┘
  *
  *           SIT1145 SPI 协议（单字节帧）：
  *           写操作：CS↓ → 发送 [addr<<1 | 0x00] → 发送 [data] → CS↑
  *           读操作：CS↓ → 发送 [addr<<1 | 0x01] → 发送 [0xFF] → 收 [data] → CS↑
  *
  *           SPI 时序：Mode 1（CPOL=0, CPHA=1），下降沿采样
  *           SPI 速率：180MHz / 128 ≈ 1.4MHz
  **************************************************************************
  */

/* 头文件 ------------------------------------------------------------------*/
#include "sit1145.h"
#include "timer_drv.h"

/* ==========================================================================
 *  私有宏定义
 * ========================================================================== */

/**
 * @brief  SPI1 时钟分频系数
 * @note   系统时钟 180MHz，APB2 = 180MHz
 *         DIV_128 分频后 SPI 时钟 = 180 / 128 ≈ 1.4MHz
 *         SIT1145 SPI 最大时钟约 5MHz，1.4MHz 安全可靠
 */
#define SIT1145_SPI_DIV             SPI_MCLK_DIV_128

/**
 * @brief  CS 引脚建立/保持延时（NOP 循环次数）
 * @note   180MHz 下每个 NOP 约 5.6ns，180 次 ≈ 1μs
 *         满足 SIT1145 数据手册要求的 CS 建立时间 tCSS ≥ 50ns
 */
#define SIT1145_CS_DELAY_CYCLES     180U

/* ==========================================================================
 *  私有延时函数
 * ========================================================================== */

/**
 * @brief  软件微秒级延时
 * @note   纯 NOP 忙等，用于 CS 引脚的建立/保持延时
 *         不依赖定时器，可在中断上下文中安全使用
 */
static void sit1145_delay_us(void)
{
  volatile uint32_t i = SIT1145_CS_DELAY_CYCLES;
  while (i-- > 0U)
  {
    __asm("nop");
  }
}

/**
 * @brief  软件毫秒级延时
 * @note   仅在初始化路径中使用（sit1145_init / sit1145_normal_mode_set）
 *         主循环中不应调用，会阻塞其他任务
 */
static void sit1145_delay_ms(uint32_t ms)
{
  uint32_t n;
  while (ms-- > 0U)
  {
    for (n = 0U; n < 1000U; n++)
    {
      sit1145_delay_us();
    }
  }
}

/* ==========================================================================
 *  SPI 底层操作
 * ========================================================================== */

/**
 * @brief  拉低 CS 引脚（选中 SIT1145）
 * @note   CS 低电平有效。拉低后延时 1μs 等待 SIT1145 准备好接收数据
 */
static void sit1145_cs_low(void)
{
  gpio_bits_reset(SIT1145_CS_GPIO_PORT, SIT1145_CS_GPIO_PIN);
  sit1145_delay_us();  /* CS 建立时间 tCSS */
}

/**
 * @brief  拉高 CS 引脚（释放 SIT1145）
 * @note   先延时 1μs 确保最后一个时钟边沿完成，再拉高 CS
 */
static void sit1145_cs_high(void)
{
  sit1145_delay_us();  /* CS 保持时间 tCSH */
  gpio_bits_set(SIT1145_CS_GPIO_PORT, SIT1145_CS_GPIO_PIN);
}

/**
 * @brief  SPI1 收发一个字节（阻塞模式）
 * @note   全双工操作：同时发送 tx_data 并接收一个字节
 *         超时保护：发送缓冲区满或接收缓冲区空时最多等待 100000 次
 *         超时返回 0xFF（与 SPI 总线空闲电平一致）
 * @param  tx_data: 要发送的字节
 * @retval 接收到的字节，超时返回 0xFF
 */
static uint8_t sit1145_spi_xfer(uint8_t tx_data)
{
  uint32_t guard = 100000U;

  /* 等待发送缓冲区空（TDBE=1 表示可以写入新数据） */
  while ((spi_i2s_flag_get(SPI1, SPI_I2S_TDBE_FLAG) == RESET) && (guard != 0U))
  {
    guard--;
  }
  if (guard == 0U)
  {
    return 0xFFU;  /* 发送超时 */
  }

  /* 写入发送数据，启动 SPI 时钟 */
  spi_i2s_data_transmit(SPI1, (uint16_t)tx_data);

  /* 等待接收缓冲区有数据（RDBF=1 表示可以读取） */
  guard = 100000U;
  while ((spi_i2s_flag_get(SPI1, SPI_I2S_RDBF_FLAG) == RESET) && (guard != 0U))
  {
    guard--;
  }
  if (guard == 0U)
  {
    return 0xFFU;  /* 接收超时 */
  }

  /* 读取接收到的数据 */
  return (uint8_t)spi_i2s_data_receive(SPI1);
}

/**
 * @brief  等待 SIT1145 收发器 CTS 位置位
 * @note   CTS（Clear To Send）在 TRANSCEIVER_STATUS 寄存器 bit7
 *         CTS=1 表示收发器已进入 Normal 模式，可以驱动 CAN 总线
 *         每次轮询间隔 1ms，总超时由 timeout_ms 控制
 * @param  timeout_ms: 最大等待时间（毫秒）
 * @retval 1=CTS 已就绪，0=超时
 */
static uint8_t sit1145_wait_cts(uint32_t timeout_ms)
{
  uint32_t t0 = timer_get_tick();
  uint8_t sta;

  do
  {
    sta = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_STATUS);
    if ((sta & SIT1145_TRAN_STA_CTS) != 0U)
    {
      return 1U;
    }
  } while ((timer_get_tick() - t0) < timeout_ms);
  return 0U;
}

/* ==========================================================================
 *  导出函数 — 寄存器读写
 * ========================================================================== */

/**
 * @brief  通过 SPI 写 SIT1145 寄存器
 * @note   SPI 写帧：CS↓ → [addr<<1 | 0] → [data] → CS↑
 *         地址左移1位，最低位=0 表示写操作
 * @param  addr: 寄存器地址（7 位，如 0x01/0x20/0x23 等）
 * @param  data: 写入值（8 位）
 */
void sit1145_write_reg(uint8_t addr, uint8_t data)
{
  sit1145_cs_low();
  sit1145_spi_xfer((uint8_t)(addr << 1) | SIT1145_WRITE);  /* 地址字节：addr<<1 | 0 */
  sit1145_spi_xfer(data);                                    /* 数据字节 */
  sit1145_cs_high();
}

/**
 * @brief  通过 SPI 读 SIT1145 寄存器
 * @note   SPI 读帧：CS↓ → [addr<<1 | 1] → [0xFF] → 收 [data] → CS↑
 *         地址左移1位，最低位=1 表示读操作
 *         第二个字节发 0xFF（dummy），同时接收寄存器数据
 * @param  addr: 寄存器地址（7 位）
 * @retval 寄存器值（8 位），SPI 超时返回 0xFF
 */
uint8_t sit1145_read_reg(uint8_t addr)
{
  uint8_t val;

  sit1145_cs_low();
  sit1145_spi_xfer((uint8_t)(addr << 1) | SIT1145_READ);  /* 地址字节：addr<<1 | 1 */
  val = sit1145_spi_xfer(0xFF);                             /* 发送 0xFF，接收寄存器数据 */
  sit1145_cs_high();

  return val;
}

/* ==========================================================================
 *  导出函数 — 模式切换
 * ========================================================================== */

/**
 * @brief  通用模式切换（带回读验证）
 * @note   向 MODE_CONTROL 寄存器写入目标模式值
 *         写入后延时 2μs 等待模式切换完成
 *         然后回读寄存器验证模式是否切换成功
 *         Sleep 模式例外：SPI 会被关闭，无法回读，直接返回成功
 * @param  target_mode: 目标模式值
 *         - SIT1145_MC_SLEEP_MODE   (0x01) 睡眠
 *         - SIT1145_MC_STANDBY_MODE (0x04) 待机
 *         - SIT1145_MC_NORMAL_MODE  (0x07) 正常
 * @retval 1=切换成功（或 Sleep 模式信任写入），0=回读不匹配
 */
static uint8_t sit1145_set_mode(uint8_t target_mode)
{
  uint8_t mode_val;

  /* 写入目标模式 */
  sit1145_write_reg(SIT1145_REG_MODE_CONTROL, target_mode);

  /* 延时 2μs 等待模式切换完成（数据手册 tMODE） */
  sit1145_delay_us();
  sit1145_delay_us();

  /* Sleep 模式会关闭 SPI 接口，无法回读验证，直接信任写入 */
  if (target_mode == SIT1145_MC_SLEEP_MODE)
  {
    return 1U;
  }

  /* 回读 MODE_CONTROL 寄存器，验证 [2:0] 位是否匹配目标模式 */
  mode_val = sit1145_read_reg(SIT1145_REG_MODE_CONTROL);
  if ((mode_val & SIT1145_MC_MODE_MASK) != target_mode)
  {
    return 0;  /* 模式切换失败 */
  }

  return 1U;  /* 模式切换成功 */
}

/**
 * @brief  切换 SIT1145 到 Normal 模式并等待 CTS 就绪
 * @note   Normal 模式下 CAN 收发器完全激活，可收发帧
 *         切换前须确保 CAN Control 和 Data Rate 寄存器已配置
 *         流程：写模式 → 等 CTS=1（最多 20ms）
 *         如果第一次切换失败，延时 1ms 后重试一次
 * @retval 1=成功（CTS=1），0=失败
 */
uint8_t sit1145_normal_mode_set(void)
{
  /* 第一次尝试切换 */
  if (sit1145_set_mode(SIT1145_MC_NORMAL_MODE) == 0U)
  {
    /* 第一次失败，延时 1ms 后重试 */
    sit1145_delay_ms(1U);
    if (sit1145_set_mode(SIT1145_MC_NORMAL_MODE) == 0U)
    {
      return 0U;  /* 两次都失败 */
    }
  }
  /* 等待 CTS 位置位（收发器就绪可驱动总线） */
  return sit1145_wait_cts(20U);
}

/**
 * @brief  切换 SIT1145 到 Standby 模式
 * @note   Standby 模式特点：
 *         - 低功耗监听状态
 *         - CAN 总线被动（不发送帧）
 *         - SPI 接口仍可访问（可读写寄存器）
 *         - 收发器监听 CAN 总线，检测到 ISO 11898-2 WUP 模式后
 *           置位 CW 标志并强制 RXD(PA11) 拉低
 * @retval 1=成功，0=失败
 */
uint8_t sit1145_standby_mode_set(void)
{
  return sit1145_set_mode(SIT1145_MC_STANDBY_MODE);
}

/**
 * @brief  切换 SIT1145 到 Sleep 模式
 * @note   Sleep 模式特点：
 *         - 最低功耗
 *         - ⚠️ SPI 接口完全不可用！调用后任何 SPI 读写都会超时
 *         - 唤醒方式：INH 引脚电平变化，或重新上电
 *         - 调用前须确保 CAN 控制器已停止
 * @retval 1 始终返回 1（SPI 已断开，无法验证）
 */
uint8_t sit1145_sleep_mode_set(void)
{
  return sit1145_set_mode(SIT1145_MC_SLEEP_MODE);
}

/**
 * @brief  获取 SIT1145 当前工作模式
 * @note   读取 MODE_CONTROL 寄存器 [2:0] 位
 *         如果 SPI 通信失败（芯片处于 Sleep），返回 0xFF
 * @retval 模式值：
 *         SIT1145_MC_SLEEP_MODE   (0x01) — Sleep
 *         SIT1145_MC_STANDBY_MODE (0x04) — Standby
 *         SIT1145_MC_NORMAL_MODE  (0x07) — Normal
 *         0xFF — SPI 读取失败
 */
uint8_t sit1145_get_mode(void)
{
  uint8_t val = sit1145_read_reg(SIT1145_REG_MODE_CONTROL);
  /* SPI 失败时（芯片处于 Sleep），spi_xfer 超时返回 0xFF */
  return val & SIT1145_MC_MODE_MASK;
}

/* ==========================================================================
 *  导出函数 — 唤醒控制
 * ========================================================================== */

/**
 * @brief  读取 SIT1145 主状态寄存器（0x03，只读）
 * @note   主状态寄存器反映芯片的全局状态：
 *         - FSMS（bit7）：Sleep 触发原因（0=SPI 指令，1=VCC 欠压）
 *         - OTWS（bit6）：过温警告（0=正常，1=温度过高）
 *         - NMS（bit5）：是否进入过 Normal（0=从未，1=至少一次）
 * @retval 主状态寄存器原始值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_main_status(void)
{
  return sit1145_read_reg(SIT1145_REG_MAIN_STATUS);
}

/**
 * @brief  判断 Sleep 模式是否由 VCC 欠压触发
 * @note   读取主状态寄存器 FSMS 位（bit7）
 * @retval 1=由欠压触发，0=由 SPI 指令触发，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_sleep_by_undervoltage(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_MAIN_STATUS);
  if (sta == 0xFFU)
  {
    return 0xFFU;  /* SPI 读取失败 */
  }
  return (sta & SIT1145_MAIN_STA_FSMS) ? 1U : 0U;
}

/**
 * @brief  查询过温警告状态
 * @note   读取主状态寄存器 OTWS 位（bit6）
 *         芯片温度超过阈值时置位（典型 150°C）
 * @retval 1=过温警告，0=温度正常，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_overtemp_warning(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_MAIN_STATUS);
  if (sta == 0xFFU)
  {
    return 0xFFU;
  }
  return (sta & SIT1145_MAIN_STA_OTWS) ? 1U : 0U;
}

/**
 * @brief  查询是否已进入过 Normal 模式
 * @note   读取主状态寄存器 NMS 位（bit5）
 *         上电后首次进入 Normal 时硬件自动置位，之后不会清零
 *         可用于验证 SIT1145 是否成功进入过 Normal
 * @retval 1=已进入过 Normal，0=从未进入，0xFF=SPI 读取失败
 */
uint8_t sit1145_has_entered_normal(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_MAIN_STATUS);
  if (sta == 0xFFU)
  {
    return 0xFFU;
  }
  return (sta & SIT1145_MAIN_STA_NMS) ? 1U : 0U;
}

/**
 * @brief  读取 CAN 控制寄存器（0x20，读写）
 * @note   返回寄存器原始值，包含 CFDC/PNCOK/CPNC/CMC 等配置位
 * @retval 寄存器原始值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_can_control(void)
{
  return sit1145_read_reg(SIT1145_REG_CAN_CONTROL);
}

/**
 * @brief  获取当前 CMC 模式值（bit[1:0]）
 * @note   读取 CAN 控制寄存器并提取 CMC 位：
 *         - SIT1145_CMC_OFFLINE    (0x00) 离线模式：CAN 收发器完全关闭
 *         - SIT1145_CMC_ACTIVE_UVLO(0x01) 主动模式（带欠压检测）
 *         - SIT1145_CMC_ACTIVE_NO_UVLO(0x02) 主动模式（不带欠压检测）
 *         - SIT1145_CMC_LISTEN_ONLY(0x03) 只听模式：仅接收不发送
 * @retval CMC 模式值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_cmc_mode(void)
{
  uint8_t val = sit1145_read_reg(SIT1145_REG_CAN_CONTROL);
  if (val == 0xFFU)
  {
    return 0xFFU;  /* SPI 读取失败 */
  }
  return val & SIT1145_CAN_CTRL_CMC_MASK;
}

/**
 * @brief  读取收发器状态寄存器（0x22，只读）
 * @note   返回收发器状态原始值，包含 CTS/CPNERR 等状态位
 * @retval 寄存器原始值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_transceiver_status(void)
{
  return sit1145_read_reg(SIT1145_REG_TRANSCEIVER_STATUS);
}

/**
 * @brief  查询 CTS 状态（收发器是否就绪可发送）
 * @note   读取收发器状态寄存器 CTS 位（bit7）
 *         CTS=1 表示收发器已进入激活模式，可驱动 CAN 总线
 *         CTS=0 表示收发器未处于激活模式（Standby/Sleep/离线）
 * @retval 1=就绪可发送，0=未就绪，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_cts_ready(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_STATUS);
  if (sta == 0xFFU)
  {
    return 0xFFU;
  }
  return (sta & SIT1145_TRAN_STA_CTS) ? 1U : 0U;
}

/**
 * @brief  查询局部网络错误状态
 * @note   读取收发器状态寄存器 CPNERR 位（bit6）
 *         本项目未使用局部网络功能，该位通常为 0
 * @retval 1=有错误，0=无错误，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_cpn_error(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_STATUS);
  if (sta == 0xFFU)
  {
    return 0xFFU;
  }
  return (sta & SIT1145_TRAN_STA_CPNERR) ? 1U : 0U;
}

/**
 * @brief  查询局部网络同步状态
 * @note   读取收发器状态寄存器 CPNS 位（bit5）
 *         本项目未使用局部网络功能，该位通常为 0
 * @retval 1=已同步，0=未同步，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_cpns_synced(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_STATUS);
  if (sta == 0xFFU)
  {
    return 0xFFU;
  }
  return (sta & SIT1145_TRAN_STA_CPNS) ? 1U : 0U;
}

/**
 * @brief  查询 CAN 振荡器状态
 * @note   读取收发器状态寄存器 COSCS 位（bit4）
 * @retval 1=振荡器就绪，0=未就绪，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_osc_ready(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_STATUS);
  if (sta == 0xFFU)
  {
    return 0xFFU;
  }
  return (sta & SIT1145_TRAN_STA_COSCS) ? 1U : 0U;
}

/**
 * @brief  查询 CAN 总线静默状态
 * @note   读取收发器状态寄存器 CBSS 位（bit3）
 *         总线静默表示无显性位活动
 * @retval 1=总线静默，0=总线有活动，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_bus_silent(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_STATUS);
  if (sta == 0xFFU)
  {
    return 0xFFU;
  }
  return (sta & SIT1145_TRAN_STA_CBSS) ? 1U : 0U;
}

/**
 * @brief  查询 VCC 电源电压状态
 * @note   读取收发器状态寄存器 VCS 位（bit1）
 *         0 = VCC 电压低于欠压阈值
 *         1 = VCC 电压正常
 * @retval 1=电压正常，0=欠压，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_vcc_ok(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_STATUS);
  if (sta == 0xFFU)
  {
    return 0xFFU;
  }
  return (sta & SIT1145_TRAN_STA_VCS) ? 1U : 0U;
}

/**
 * @brief  查询 CAN 故障状态
 * @note   读取收发器状态寄存器 CFS 位（bit0）
 * @retval 1=有故障，0=无故障，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_can_fault(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_STATUS);
  if (sta == 0xFFU)
  {
    return 0xFFU;
  }
  return (sta & SIT1145_TRAN_STA_CFS) ? 1U : 0U;
}

/**
 * @brief  配置选择性唤醒帧（WUF）
 * @note   写入 WUF 的 ID 寄存器、掩码寄存器、数据掩码寄存器和帧控制寄存器
 *         标准帧(11位)：ID 存入 REG2/REG3，REG0/REG1 = 0
 *         扩展帧(29位)：ID 存入 REG0~REG3
 *         掩码中 1=无关位（任意值匹配），0=必须精确匹配
 * @param  cfg: WUF 配置结构体指针
 * @retval 1=配置成功（回读匹配），0=配置失败
 */
uint8_t sit1145_wuf_config(const sit1145_wuf_cfg_t *cfg)
{
  uint8_t frame_ctrl;
  uint8_t id0, id1, id2, id3;
  uint8_t m0, m1, m2, m3;

  if (cfg == (const sit1145_wuf_cfg_t *)0)
  {
    return 0;
  }

  /* 计算帧控制寄存器值：IDE + DLC + PNDM */
  frame_ctrl = (uint8_t)(cfg->dlc & SIT1145_WUF_DLC_MASK);
  if (cfg->ide != 0U)
  {
    frame_ctrl |= SIT1145_WUF_IDE;  /* 扩展帧 */
  }
  if (cfg->pndm != 0U)
  {
    frame_ctrl |= SIT1145_WUF_PNDM;  /* 评估数据字段 */
  }

  /* 计算 ID 寄存器值 */
  if (cfg->ide != 0U)
  {
    /* 扩展帧 29 位：ID[28:21]/ID[20:13]/ID[12:5]/ID[4:0] */
    id0 = (uint8_t)((cfg->can_id >> 21) & 0xFFU);
    id1 = (uint8_t)((cfg->can_id >> 13) & 0xFFU);
    id2 = (uint8_t)((cfg->can_id >> 5) & 0xFFU);
    id3 = (uint8_t)((cfg->can_id & 0x1FU) << 3);
    m0 = (uint8_t)((cfg->can_id_mask >> 21) & 0xFFU);
    m1 = (uint8_t)((cfg->can_id_mask >> 13) & 0xFFU);
    m2 = (uint8_t)((cfg->can_id_mask >> 5) & 0xFFU);
    m3 = (uint8_t)((cfg->can_id_mask & 0x1FU) << 3);
  }
  else
  {
    /* 标准帧 11 位：ID[10:3]/ID[2:0] 存入 REG2/REG3 */
    id0 = 0U;
    id1 = 0U;
    id2 = (uint8_t)((cfg->can_id >> 3) & 0xFFU);
    id3 = (uint8_t)((cfg->can_id & 0x07U) << 5);
    m0 = 0U;
    m1 = 0U;
    m2 = (uint8_t)((cfg->can_id_mask >> 3) & 0xFFU);
    m3 = (uint8_t)((cfg->can_id_mask & 0x07U) << 5);
  }

  /* 写入帧控制寄存器 */
  sit1145_write_reg(SIT1145_REG_FRAME_CTRL, frame_ctrl);

  /* 写入 ID 寄存器 */
  sit1145_write_reg(SIT1145_REG_ID_REG0, id0);
  sit1145_write_reg(SIT1145_REG_ID_REG1, id1);
  sit1145_write_reg(SIT1145_REG_ID_REG2, id2);
  sit1145_write_reg(SIT1145_REG_ID_REG3, id3);

  /* 写入 ID 掩码寄存器 */
  sit1145_write_reg(SIT1145_REG_MASK_REG0, m0);
  sit1145_write_reg(SIT1145_REG_MASK_REG1, m1);
  sit1145_write_reg(SIT1145_REG_MASK_REG2, m2);
  sit1145_write_reg(SIT1145_REG_MASK_REG3, m3);

  /* 写入数据掩码寄存器 */
  sit1145_write_reg(SIT1145_REG_DATA_MASK0, cfg->data_mask[0]);
  sit1145_write_reg(SIT1145_REG_DATA_MASK1, cfg->data_mask[1]);
  sit1145_write_reg(SIT1145_REG_DATA_MASK2, cfg->data_mask[2]);
  sit1145_write_reg(SIT1145_REG_DATA_MASK3, cfg->data_mask[3]);
  sit1145_write_reg(SIT1145_REG_DATA_MASK4, cfg->data_mask[4]);
  sit1145_write_reg(SIT1145_REG_DATA_MASK5, cfg->data_mask[5]);
  sit1145_write_reg(SIT1145_REG_DATA_MASK6, cfg->data_mask[6]);
  sit1145_write_reg(SIT1145_REG_DATA_MASK7, cfg->data_mask[7]);

  /* 回读帧控制寄存器验证 */
  if (sit1145_read_reg(SIT1145_REG_FRAME_CTRL) != frame_ctrl)
  {
    return 0;
  }

  return 1;
}

/**
 * @brief  使能选择性唤醒（CPNC=1 + PNCOK=1 + CWE=1）
 * @note   在 sit1145_wuf_config() 之后调用
 *         先设 CPNC=1，再设 PNCOK=1（PNCOK 置1后配置寄存器锁定）
 *         最后使能 CWE
 */
void sit1145_wuf_enable(void)
{
  uint8_t can_ctrl;

  /* 读取当前 CAN 控制寄存器 */
  can_ctrl = sit1145_read_reg(SIT1145_REG_CAN_CONTROL);
  if (can_ctrl == 0xFFU)
  {
    return;  /* SPI 失败 */
  }

  /* 设置 CPNC=1（选择性唤醒使能） */
  can_ctrl |= (1U << SIT1145_CAN_CTRL_CPNC_POS);
  sit1145_write_reg(SIT1145_REG_CAN_CONTROL, can_ctrl);

  /* 设置 PNCOK=1（配置有效，锁定配置寄存器） */
  can_ctrl |= (1U << SIT1145_CAN_CTRL_PNCOK_POS);
  sit1145_write_reg(SIT1145_REG_CAN_CONTROL, can_ctrl);

  /* 使能 CWE */
  sit1145_write_reg(SIT1145_REG_TRANSCEIVER_EVENT_EN, SIT1145_CWE);

  /* 清除残留的 WUF/CW 标志 */
  sit1145_write_reg(SIT1145_REG_TRANSCEIVER_EVENT, SIT1145_CW | SIT1145_WUF);
}

/**
 * @brief  关闭选择性唤醒（CPNC=0），恢复标准唤醒模式
 * @note   CWE 保持不变，仍可被标准 WUP 模式唤醒
 */
void sit1145_wuf_disable(void)
{
  uint8_t can_ctrl;

  can_ctrl = sit1145_read_reg(SIT1145_REG_CAN_CONTROL);
  if (can_ctrl == 0xFFU)
  {
    return;
  }

  /* 清除 CPNC 位 */
  can_ctrl &= ~(1U << SIT1145_CAN_CTRL_CPNC_POS);
  sit1145_write_reg(SIT1145_REG_CAN_CONTROL, can_ctrl);
}


/**
 * @brief  读取收发器事件状态寄存器（0x63，读/写，W1C）
 * @note   返回收发器事件状态原始值，包含 PNFDE/CBS/CF/CW
 * @retval 寄存器原始值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_trx_event_status(void)
{
  return sit1145_read_reg(SIT1145_REG_TRX_EVENT_STATUS);
}

/**
 * @brief  查询局部网络帧检测错误
 * @note   读取收发器事件状态寄存器 PNFDE 位（bit5），W1C 标志
 * @retval 1=有错误，0=无错误，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_pnf_error(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_TRX_EVENT_STATUS);
  if (sta == 0xFFU) { return 0xFFU; }
  return (sta & SIT1145_TRX_EVT_STA_PNFDE) ? 1U : 0U;
}

/**
 * @brief  查询 CAN 总线静默状态（事件）
 * @note   读取收发器事件状态寄存器 CBS 位（bit4），W1C 标志
 *         总线在 tB(silence) 时间内无活动时置位
 * @retval 1=总线静默，0=总线有活动，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_bus_silence(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_TRX_EVENT_STATUS);
  if (sta == 0xFFU) { return 0xFFU; }
  return (sta & SIT1145_TRX_EVT_STA_CBS) ? 1U : 0U;
}

/**
 * @brief  查询 CAN 故障事件
 * @note   读取收发器事件状态寄存器 CF 位（bit1），W1C 标志
 *         仅在 Normal 模式 CMC=01 时使能
 * @retval 1=有故障事件，0=无故障，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_cf_event(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_TRX_EVENT_STATUS);
  if (sta == 0xFFU) { return 0xFFU; }
  return (sta & SIT1145_TRX_EVT_STA_CF) ? 1U : 0U;
}

/**
 * @brief  查询 CAN 唤醒事件
 * @note   读取收发器事件状态寄存器 CW 位（bit0），W1C 标志
 * @retval 1=有唤醒事件，0=无唤醒，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_cw_event(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_TRX_EVENT_STATUS);
  if (sta == 0xFFU) { return 0xFFU; }
  return (sta & SIT1145_TRX_EVT_STA_CW) ? 1U : 0U;
}

/**
 * @brief  读取唤醒引脚事件状态寄存器（0x64，读/写，W1C）
 * @note   返回唤醒引脚事件状态原始值，包含 WPR/WPF
 * @retval 寄存器原始值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_wake_pin_event_status(void)
{
  return sit1145_read_reg(SIT1145_REG_WAKE_PIN_EVENT_STATUS);
}

/**
 * @brief  查询 WAKE 引脚上升沿事件
 * @note   读取唤醒引脚事件状态寄存器 WPR 位（bit1），W1C 标志
 * @retval 1=检测到上升沿，0=无，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_wake_rising(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_WAKE_PIN_EVENT_STATUS);
  if (sta == 0xFFU) { return 0xFFU; }
  return (sta & SIT1145_WAKE_EVT_WPR) ? 1U : 0U;
}

/**
 * @brief  查询 WAKE 引脚下降沿事件
 * @note   读取唤醒引脚事件状态寄存器 WPF 位（bit0），W1C 标志
 * @retval 1=检测到下降沿，0=无，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_wake_falling(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_WAKE_PIN_EVENT_STATUS);
  if (sta == 0xFFU) { return 0xFFU; }
  return (sta & SIT1145_WAKE_EVT_WPF) ? 1U : 0U;
}

/**
 * @brief  读取全局事件状态寄存器（0x60，只读）
 * @note   返回全局事件状态原始值，包含 WPE/TRXE/SYSE 等挂起标志
 * @retval 寄存器原始值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_global_event_status(void)
{
  return sit1145_read_reg(SIT1145_REG_GLOBAL_EVENT_STATUS);
}

/**
 * @brief  查询 WAKE 引脚事件是否挂起
 * @note   读取全局事件状态寄存器 WPE 位（bit3）
 * @retval 1=有事件挂起，0=无，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_wake_pin_event(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_GLOBAL_EVENT_STATUS);
  if (sta == 0xFFU) { return 0xFFU; }
  return (sta & SIT1145_GLOBAL_STA_WPE) ? 1U : 0U;
}

/**
 * @brief  查询收发器事件是否挂起
 * @note   读取全局事件状态寄存器 TRXE 位（bit2）
 * @retval 1=有事件挂起，0=无，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_trx_event_pending(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_GLOBAL_EVENT_STATUS);
  if (sta == 0xFFU) { return 0xFFU; }
  return (sta & SIT1145_GLOBAL_STA_TRXE) ? 1U : 0U;
}

/**
 * @brief  查询系统事件是否挂起
 * @note   读取全局事件状态寄存器 SYSE 位（bit0）
 * @retval 1=有事件挂起，0=无，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_sys_event_pending(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_GLOBAL_EVENT_STATUS);
  if (sta == 0xFFU) { return 0xFFU; }
  return (sta & SIT1145_GLOBAL_STA_SYSE) ? 1U : 0U;
}

/**
 * @brief  读取系统事件状态寄存器（0x61，只读）
 * @note   返回系统事件状态原始值，包含 PO/OTW/SPIF 等状态位
 * @retval 寄存器原始值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_system_event_status(void)
{
  return sit1145_read_reg(SIT1145_REG_SYSTEM_EVENT_STATUS);
}

/**
 * @brief  查询上电状态
 * @note   读取系统事件状态寄存器 PO 位（bit4）
 *         读取后硬件自动清零
 * @retval 1=首次上电，0=非首次，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_power_on(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_SYSTEM_EVENT_STATUS);
  if (sta == 0xFFU) { return 0xFFU; }
  return (sta & SIT1145_SYS_STA_PO) ? 1U : 0U;
}

/**
 * @brief  查询系统级过温警告
 * @note   读取系统事件状态寄存器 OTW 位（bit2）
 * @retval 1=过温，0=正常，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_sys_overtemp(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_SYSTEM_EVENT_STATUS);
  if (sta == 0xFFU) { return 0xFFU; }
  return (sta & SIT1145_SYS_STA_OTW) ? 1U : 0U;
}

uint8_t sit1145_is_spi_fault(void)
{
  uint8_t sta = sit1145_read_reg(SIT1145_REG_SYSTEM_EVENT_STATUS);
  if (sta == 0xFFU)
  {
    return 0xFFU;
  }
  return (sta & SIT1145_SYS_STA_SPIF) ? 1U : 0U;
}

/**
 * @brief  使能标准 CAN 唤醒（CWE=1）
 * @note   向 TRANSCEIVER_EVENT_EN 寄存器写入 CWE 位
 *         使能后 SIT1145 在 Standby 模式下监听 CAN 总线
 *         检测到 ISO 11898-2 WUP 模式（显性→隐性→显性序列）后
 *         置位 TRANSCEIVER_EVENT 的 CW 标志
 *         注意：此函数不改变选择性唤醒（CPNC）的状态
 */
void sit1145_wake_enable(void)
{
  sit1145_write_reg(SIT1145_REG_TRANSCEIVER_EVENT_EN, SIT1145_CWE);
}

/**
 * @brief  检测是否有 CAN 唤醒事件挂起
 * @note   三重检测机制：
 *         1. 先查 PA11 引脚电平（快速路径，无 SPI 开销）
 *            - Standby 模式下 SIT1145 在整个唤醒事件期间强制 RXD(PA11) 拉低
 *            - 需要 PA11 配置为 GPIO 输入模式才能正确读取
 *         2. 再查 SPI 寄存器 CW 位（标准 WUP 唤醒）
 *         3. 再查 SPI 寄存器 WUF 位（选择性唤醒帧）
 *            - 读 TRANSCEIVER_EVENT 寄存器的 CW 位（bit0）
 *            - CW=1 表示检测到 WUP 模式
 *            - 注意：CW 是 W1C 标志，读取后需要手动清除
 *         如果 SPI 读取返回 0xFF（超时），视为无唤醒事件
 * @retval 1=有唤醒事件，0=无
 */
uint8_t sit1145_wakeup_pending(void)
{
  uint8_t ev;

  /* 调用方已做过进 Standby 后的 inhibit。
   * PA11 低 = SIT1145 正在报 WUP，比 SPI 读 CW 更快，便于赶上主机重发。 */
  if (gpio_input_data_bit_read(GPIOA, GPIO_PINS_11) == RESET)
  {
    return 1U;
  }

  ev = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_EVENT);
  if ((ev != 0xFFU) && ((ev & (SIT1145_CW | SIT1145_WUF)) != 0U))
  {
    return 1U;
  }

  ev = sit1145_read_reg(SIT1145_REG_TRX_EVENT_STATUS);
  if ((ev != 0xFFU) && ((ev & SIT1145_TRX_EVT_STA_CW) != 0U))
  {
    return 1U;
  }
  return 0U;
}

/**
 * @brief  清除 CAN 唤醒事件标志
 * @note   向 TRANSCEIVER_EVENT 寄存器的 CW 位写 1（W1C 机制）
 *         硬件自动清零该标志
 *         进入 Standby 前和唤醒后都应调用此函数
 */
/**
 * @brief  清除 CAN 唤醒事件标志（CW + WUF，写1清零）
 * @note   向 TRANSCEIVER_EVENT 寄存器的 CW 和 WUF 位写1，
 *         硬件自动清零（W1C 机制）。
 *         进入 Standby 前和唤醒后都应调用此函数。
 */
void sit1145_wakeup_clear(void)
{
  sit1145_write_reg(SIT1145_REG_TRANSCEIVER_EVENT, SIT1145_CW | SIT1145_WUF);
  sit1145_write_reg(SIT1145_REG_TRX_EVENT_STATUS, SIT1145_TRX_EVT_STA_CW);
}

/**
 * @brief  清除 WUF 唤醒帧事件标志（写1清零）
 * @note   仅清除 WUF 位，不影响 CW 位
 *         用于选择性唤醒场景下单独清零
 */
void sit1145_wuf_clear(void)
{
  sit1145_write_reg(SIT1145_REG_TRANSCEIVER_EVENT, SIT1145_WUF);
}

/* ==========================================================================
 *  导出函数 — 初始化
 * ========================================================================== */

/**
 * @brief  初始化 SIT1145 CAN 收发器
 * @note   完整初始化流程（10 步）：
 *         1. 使能 GPIOA/GPIOB/SPI1 外设时钟
 *         2. 配置 SPI1 数据引脚（PA5=SCK, PA6=MISO, PA7=MOSI）
 *         3. 配置 CS 引脚（PA4，GPIO 输出，默认高=未选中）
 *         4. 配置悬空引脚 PB3（输出低防悬空）
 *         5. 初始化 SPI1（Mode 1, 8bit, MSB, 软件 CS, DIV_128）
 *         6. 读识别码 0x7E 验证 SPI 通信（0x70=AQ, 0x74=AQ-FD）
 *         7. 配置 CAN Control 寄存器（CFDC=1, CMC=01）
 *         8. 配置 Data Rate 寄存器（250kbps）
 *         9. 使能标准 CAN 唤醒（CWE=1）
 *        10. 进入 Standby 模式（APP 低功耗默认状态）
 * @retval 1=初始化成功，0=失败（SPI 通信异常或寄存器回读不匹配）
 */
uint8_t sit1145_init(void)
{
  gpio_init_type gpio_init_struct;
  spi_init_type spi_init_struct;
  uint8_t can_ctrl_val;
  uint8_t chip_id;

  /* ---- 步骤1：使能外设时钟 ---- */
  crm_periph_clock_enable(CRM_GPIOA_PERIPH_CLOCK, TRUE);   /* GPIOA 时钟（PA4/5/6/7/11/12） */
  crm_periph_clock_enable(CRM_GPIOB_PERIPH_CLOCK, TRUE);   /* GPIOB 时钟（PB3） */
  crm_periph_clock_enable(CRM_SPI1_PERIPH_CLOCK, TRUE);    /* SPI1 外设时钟 */

  /* ---- 步骤2：配置 SPI1 数据引脚 ---- */
  /* PA5(SCK) 和 PA7(MOSI)：推挽输出，无上下拉 */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = GPIO_PINS_5 | GPIO_PINS_7;
  gpio_init_struct.gpio_mode           = GPIO_MODE_MUX;        /* 复用功能模式 */
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL; /* 推挽输出 */
  gpio_init_struct.gpio_pull           = GPIO_PULL_NONE;       /* 无上下拉 */
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(GPIOA, &gpio_init_struct);

  /* PA6(MISO)：复用输入，上拉（防止 SIT1145 未驱动时引脚悬空产生噪声） */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = GPIO_PINS_6;
  gpio_init_struct.gpio_mode           = GPIO_MODE_MUX;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_UP;         /* 上拉防悬空 */
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(GPIOA, &gpio_init_struct);

  /* 引脚复用映射：PA5/PA6/PA7 → SPI1（AF MUX0） */
  gpio_pin_mux_config(GPIOA, GPIO_PINS_SOURCE5, GPIO_MUX_0);  /* PA5 → SPI1_SCK */
  gpio_pin_mux_config(GPIOA, GPIO_PINS_SOURCE6, GPIO_MUX_0);  /* PA6 → SPI1_MISO */
  gpio_pin_mux_config(GPIOA, GPIO_PINS_SOURCE7, GPIO_MUX_0);  /* PA7 → SPI1_MOSI */

  /* ---- 步骤3：配置 CS 引脚（PA4，GPIO 输出，默认高电平=未选中）---- */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = SIT1145_CS_GPIO_PIN;  /* PA4 */
  gpio_init_struct.gpio_mode           = GPIO_MODE_OUTPUT;     /* GPIO 输出模式 */
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_NONE;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(SIT1145_CS_GPIO_PORT, &gpio_init_struct);
  gpio_bits_set(SIT1145_CS_GPIO_PORT, SIT1145_CS_GPIO_PIN);   /* CS 拉高（未选中） */

  /* ---- 步骤4：配置悬空引脚 PB3（输出低防悬空）---- */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = SIT1145_FLOAT_GPIO_PIN; /* PB3 */
  gpio_init_struct.gpio_mode           = GPIO_MODE_OUTPUT;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_NONE;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(SIT1145_FLOAT_GPIO_PORT, &gpio_init_struct);
  gpio_bits_reset(SIT1145_FLOAT_GPIO_PORT, SIT1145_FLOAT_GPIO_PIN); /* PB3 输出低 */

  /* ---- 步骤5：初始化 SPI1 ---- */
  spi_default_para_init(&spi_init_struct);
  spi_init_struct.transmission_mode      = SPI_TRANSMIT_FULL_DUPLEX; /* 全双工 */
  spi_init_struct.master_slave_mode      = SPI_MODE_MASTER;          /* 主机模式 */
  spi_init_struct.mclk_freq_division     = SIT1145_SPI_DIV;          /* DIV_128 ≈ 1.4MHz */
  spi_init_struct.first_bit_transmission = SPI_FIRST_BIT_MSB;         /* MSB 先发 */
  spi_init_struct.frame_bit_num          = SPI_FRAME_8BIT;            /* 8 位帧 */
  spi_init_struct.clock_polarity         = SPI_CLOCK_POLARITY_LOW;    /* CPOL=0 */
  spi_init_struct.clock_phase            = SPI_CLOCK_PHASE_2EDGE;     /* CPHA=1，下降沿采样 */
  spi_init_struct.cs_mode_selection      = SPI_CS_SOFTWARE_MODE;      /* 软件控制 CS */
  spi_init(SPI1, &spi_init_struct);

  /* 使能 SPI1 外设 */
  spi_enable(SPI1, TRUE);
  sit1145_delay_ms(1U);  /* 等待 SPI 稳定 */

  /* ---- 步骤6：读识别码寄存器 0x7E，验证 SPI 通信 ---- */
  chip_id = sit1145_read_reg(SIT1145_REG_IDENTIFICATION);
  if ((chip_id != SIT1145_ID_AQ) && (chip_id != SIT1145_ID_AQ_FD))
  {
    return 0;  /* SPI 通信失败或芯片型号不匹配 */
  }

  /* ---- 步骤7：配置 CAN Control 寄存器（0x20）----
   *   bit6 CFDC=1：CAN FD 容忍使能（忽略 FD 帧，不报错）
   *   bit5 PNCOK=0：局部网络配置无效
   *   bit4 CPNC=0：选择性唤醒关闭（不用 WUF，只用标准 WUP）
   *   bit[1:0] CMC=01：正常模式，VCC 欠压自动恢复
   *   必须在切换到 Normal 模式之前完成配置 */
  can_ctrl_val = SIT1145_CAN_FD_TOLERANCE_EN    /* CFDC=1 */
               | SIT1145_CAN_NETW_INVALID       /* PNCOK=0 */
               | SIT1145_SEL_WAKEUP_DIS         /* CPNC=0 */
               | SIT1145_CAN_MODE_NORMAL;        /* CMC=01 */
  sit1145_write_reg(SIT1145_REG_CAN_CONTROL, can_ctrl_val);
  /* 回读验证 */
  if (sit1145_read_reg(SIT1145_REG_CAN_CONTROL) != can_ctrl_val)
  {
    return 0;  /* 寄存器写入失败 */
  }

  /* ---- 步骤8：配置 Data Rate 寄存器（0x26）= 250kbps ---- */
  sit1145_write_reg(SIT1145_REG_DATA_RATE, SIT1145_DEFAULT_DRATE);
  /* 回读验证 */
  if (sit1145_read_reg(SIT1145_REG_DATA_RATE) != SIT1145_DEFAULT_DRATE)
  {
    return 0;  /* 寄存器写入失败 */
  }

  /* ---- 步骤9：使能标准 CAN 唤醒（ISO 11898-2 WUP）----
   *   CWE=1：使能 CAN 唤醒检测
   *   Standby 模式下 SIT1145 监听 CAN 总线
   *   任意 250kbps 帧的 SOF+仲裁段会触发 WUP 模式检测 */
  sit1145_wake_enable();    /* 写 CWE=1 到 EVENT_EN 寄存器 */
  sit1145_wakeup_clear();   /* 清除可能残留的 CW 标志 */

  /* ---- 步骤10：进入 Standby 模式（APP 低功耗默认状态）----
   *   Standby 下 SPI 仍可访问，CAN 总线被动监听
   *   失败时重试一次 */
  if (sit1145_standby_mode_set() == 0U)
  {
    sit1145_delay_ms(1U);
    if (sit1145_standby_mode_set() == 0U)
    {
      return 0;  /* 两次都失败 */
    }
  }

  return 1;  /* 初始化成功 */
}
