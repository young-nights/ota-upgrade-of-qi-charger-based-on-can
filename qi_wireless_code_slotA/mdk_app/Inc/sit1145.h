/**
  **************************************************************************
  * @file     sit1145.h
  * @brief    SIT1145 CAN FD 收发器驱动（AT32F426）
  * @details  本头文件定义 SIT1145 收发器的全部寄存器地址、位掩码、
  *           模式常量及对外接口。硬件通过 SPI1 与 MCU 通信。
  *
  *           工作模式一览：
  *           ┌─────────┬────────┬────────┬──────────────┐
  *           │ 模式    │ 功耗   │ SPI    │ CAN 收发     │
  *           ├─────────┼────────┼────────┼──────────────┤
  *           │ Normal  │ 高     │ ✅ 可用 │ ✅ 收发      │
  *           │ Standby │ 低     │ ✅ 可用 │ ❌ 仅监听唤醒 │
  *           │ Sleep   │ 最低   │ ❌ 不可用│ ❌ 完全关闭  │
  *           └─────────┴────────┴────────┴──────────────┘
  **************************************************************************
  */

#ifndef __SIT1145_H
#define __SIT1145_H

#ifdef __cplusplus
extern "C" {
#endif

/* 头文件 ------------------------------------------------------------------*/
#include "at32f422_426.h"

/* ==========================================================================
 *  引脚定义
 * ========================================================================== */

/** @brief SIT1145 SPI 片选引脚（PA4，低电平有效，软件控制） */
#define SIT1145_CS_GPIO_PORT        GPIOA
#define SIT1145_CS_GPIO_PIN         GPIO_PINS_4

/** @brief SIT1145 悬空引脚（PB3，硬件未使用，输出低防止悬空） */
#define SIT1145_FLOAT_GPIO_PORT     GPIOB
#define SIT1145_FLOAT_GPIO_PIN      GPIO_PINS_3

/* ==========================================================================
 *  SPI 读写协议
 *  SIT1145 SPI 帧格式：
 *    写：CS↓ → [addr<<1 | 0] → [data] → CS↑
 *    读：CS↓ → [addr<<1 | 1] → [dummy, 收 data] → CS↑
 * ========================================================================== */

/** @brief SPI 写标志（地址字节最低位=0） */
#define SIT1145_WRITE               0x00

/** @brief SPI 读标志（地址字节最低位=1） */
#define SIT1145_READ                0x01

/* ==========================================================================
 *  芯片识别码（数据手册表28）
 *  上电后读 0x7E 寄存器验证 SPI 通信是否正常
 * ========================================================================== */

#define SIT1145_ID_AQ               0x70  /**< SIT1145AQ 识别码 */
#define SIT1145_ID_AQ_FD            0x74  /**< SIT1145AQ/FD 识别码（支持 CAN FD） */

/* ==========================================================================
 *  寄存器地址
 *  SIT1145 共 8 个可访问寄存器，地址宽度 7 位
 * ========================================================================== */

#define SIT1145_REG_MODE_CONTROL          0x01  /**< 模式控制（读写）：选择 Sleep/Standby/Normal */
#define SIT1145_REG_MAIN_STATUS           0x03  /**< 主状态（只读）：FSMS/OTWS/NMS */
#define SIT1145_REG_CAN_CONTROL           0x20  /**< CAN 控制（读写）：CMC/CFDC/PNCOK/CPNC 配置 */
#define SIT1145_REG_TRANSCEIVER_STATUS    0x22  /**< 收发器状态（只读）：CTS 等状态位 */
#define SIT1145_REG_TRANSCEIVER_EVENT_EN  0x23  /**< 事件使能（读写）：CWE/WUE 等唤醒使能位 */
#define SIT1145_REG_TRANSCEIVER_EVENT     0x24  /**< 事件标志（读写，W1C）：CW/WUF 等唤醒标志 */
#define SIT1145_REG_DATA_RATE             0x26  /**< 数据速率（读写）：CAN 波特率配置 */
#define SIT1145_REG_ID_REG0               0x27  /**< WUF ID 寄存器0（读写）：扩展ID[28:21] */
#define SIT1145_REG_ID_REG1               0x28  /**< WUF ID 寄存器1（读写）：扩展ID[20:13] */
#define SIT1145_REG_ID_REG2               0x29  /**< WUF ID 寄存器2（读写）：标准ID[10:3] 或 扩展ID[12:5] */
#define SIT1145_REG_ID_REG3               0x2A  /**< WUF ID 寄存器3（读写）：标准ID[2:0]+RTR+IDE 或 扩展ID[4:0] */
#define SIT1145_REG_MASK_REG0             0x2B  /**< WUF ID 掩码寄存器0（读写） */
#define SIT1145_REG_MASK_REG1             0x2C  /**< WUF ID 掩码寄存器1（读写） */
#define SIT1145_REG_MASK_REG2             0x2D  /**< WUF ID 掩码寄存器2（读写） */
#define SIT1145_REG_MASK_REG3             0x2E  /**< WUF ID 掩码寄存器3（读写） */
#define SIT1145_REG_FRAME_CTRL            0x21  /**< WUF 帧控制（读写）：IDE/DLC/PNDM */
#define SIT1145_REG_DATA_MASK0            0x30  /**< WUF 数据掩码寄存器0（读写） */
#define SIT1145_REG_DATA_MASK1            0x31  /**< WUF 数据掩码寄存器1（读写） */
#define SIT1145_REG_DATA_MASK2            0x32  /**< WUF 数据掩码寄存器2（读写） */
#define SIT1145_REG_DATA_MASK3            0x33  /**< WUF 数据掩码寄存器3（读写） */
#define SIT1145_REG_DATA_MASK4            0x34  /**< WUF 数据掩码寄存器4（读写） */
#define SIT1145_REG_DATA_MASK5            0x35  /**< WUF 数据掩码寄存器5（读写） */
#define SIT1145_REG_DATA_MASK6            0x36  /**< WUF 数据掩码寄存器6（读写） */
#define SIT1145_REG_DATA_MASK7            0x37  /**< WUF 数据掩码寄存器7（读写） */
#define SIT1145_REG_IDENTIFICATION        0x7E  /**< 芯片识别码（只读）：0x70=AQ，0x74=AQ-FD */
#define SIT1145_REG_GLOBAL_EVENT_STATUS   0x60  /**< 全局事件状态（只读）：WPE/TRXE/SYSE */
#define SIT1145_REG_SYSTEM_EVENT_STATUS   0x61  /**< 系统事件状态（只读）：PO/OTW/SPIF */
#define SIT1145_REG_TRX_EVENT_STATUS      0x63  /**< 收发器事件状态（只读）：PNFDE/CBS/CF/CW */
#define SIT1145_REG_WAKE_PIN_EVENT_STATUS 0x64  /**< 唤醒引脚事件状态（只读）：WPS */

/* ==========================================================================
 *  全局事件状态寄存器位定义（0x60，只读）— 数据手册表14
 *  任一事件位为 1 表示对应类别有事件挂起
 * ========================================================================== */

/**
 * @brief WPE 位（bit3）：WAKE 引脚事件挂起
 * @note  1 = WAKE 引脚有事件挂起，0 = 无
 */
#define SIT1145_GLOBAL_STA_WPE      (1U << 3)

/**
 * @brief TRXE 位（bit2）：收发器事件挂起
 * @note  1 = 收发器有事件挂起（CW/WUF 等），0 = 无
 */
#define SIT1145_GLOBAL_STA_TRXE     (1U << 2)

/**
 * @brief SYSE 位（bit0）：系统事件挂起
 * @note  1 = 系统有事件挂起（PO/OTW/SPIF 等），0 = 无
 */
#define SIT1145_GLOBAL_STA_SYSE     (1U << 0)

/* ==========================================================================
 *  系统事件状态寄存器位定义（0x61，只读）— 数据手册表15
 *  系统级事件状态，需通过读取全局事件状态寄存器确认
 * ========================================================================== */

/**
 * @brief PO 位（bit4）：Power-On 上电状态
 * @note  1 = 电池上电后已退出关闭模式（首次上电）
 *        0 = 非首次上电（热复位等）
 *        读取后自动清零
 */
#define SIT1145_SYS_STA_PO          (1U << 4)

/**
 * @brief OTW 位（bit2）：Over-Temperature Warning 过温警告
 * @note  1 = 芯片温度超过过温警告阈值
 *        0 = 温度正常
 */
#define SIT1145_SYS_STA_OTW         (1U << 2)

/**
 * @brief SPIF 位（bit1）：SPI Fault SPI 故障
 * @note  1 = 检测到 SPI 通信故障
 *        0 = SPI 正常
 */
#define SIT1145_SYS_STA_SPIF        (1U << 1)

/* ==========================================================================
 *  收发器状态寄存器位定义（0x22，只读）— 数据手册表5
 * ========================================================================== */

/**
 * @brief CTS 位（bit7）：Clear To Send
 * @note  1 = 收发器已进入激活模式（Normal），可驱动 CAN 总线发送帧
 *        0 = 收发器未处于激活模式（Standby/Sleep/离线）
 */
#define SIT1145_TRAN_STA_CTS        (1U << 7)

/**
 * @brief CPNERR 位（bit6）：CAN Partial Networking Error
 * @note  局部网络错误标志（只读）：
 *        0 = 未检测到 CAN 局部网络错误（PNFDE=0 且 PNCOK=1 时）
 *        1 = 检测到局部网络错误
 *        本项目未使用局部网络功能，该位通常为 0
 */
#define SIT1145_TRAN_STA_CPNERR     (1U << 6)

/**
 * @brief CPNS 位（bit5）：CAN Partial Networking Status
 * @note  局部网络状态（只读）：
 *        0 = 局部网络未同步
 *        1 = 局部网络已同步
 *        本项目未使用局部网络功能，该位通常为 0
 */
#define SIT1145_TRAN_STA_CPNS       (1U << 5)

/**
 * @brief COSCS 位（bit4）：CAN Oscillator Status
 * @note  CAN 振荡器状态（只读）：
 *        0 = CAN 振荡器未就绪
 *        1 = CAN 振荡器已就绪
 */
#define SIT1145_TRAN_STA_COSCS      (1U << 4)

/**
 * @brief CBSS 位（bit3）：CAN Bus Silent Status
 * @note  CAN 总线静默状态（只读）：
 *        0 = 总线未处于静默
 *        1 = 总线处于静默状态（无显性位）
 */
#define SIT1145_TRAN_STA_CBSS       (1U << 3)

/**
 * @brief VCS 位（bit1）：VCC Power Voltage Status
 * @note  VCC 电源电压状态（只读）：
 *        0 = VCC 电压低于欠压阈值（欠压）
 *        1 = VCC 电压正常
 */
#define SIT1145_TRAN_STA_VCS        (1U << 1)

/**
 * @brief CFS 位（bit0）：CAN Fault Status
 * @note  CAN 故障状态（只读）：
 *        0 = 无 CAN 故障
 *        1 = 检测到 CAN 故障
 */
#define SIT1145_TRAN_STA_CFS        (1U << 0)

/* ==========================================================================
 *  主状态寄存器位定义（0x03，只读）— 数据手册表3
 *  上电后默认 0x00
 * ========================================================================== */

/**
 * @brief FSMS 位（bit7）：Fail-Safe Mode Status
 * @note  指示进入 Sleep 模式的原因：
 *        0 = 由 SPI 指令（写 MODE_CONTROL=0x01）触发
 *        1 = 由 VCC 欠压（undervoltage）自动触发
 */
#define SIT1145_MAIN_STA_FSMS       (1U << 7)

/**
 * @brief OTWS 位（bit6）：Over-Temperature Warning Status
 * @note  过温警告状态：
 *        0 = 温度正常
 *        1 = 芯片温度超过过温警告阈值（典型 150°C）
 *        在 Normal 模式下持续监测，过温时收发器可能自动保护
 */
#define SIT1145_MAIN_STA_OTWS       (1U << 6)

/**
 * @brief NMS 位（bit5）：Normal Mode Status
 * @note  指示上电后是否已进入过 Normal 模式：
 *        0 = 上电后从未进入 Normal 模式
 *        1 = 自上电以来至少进入过一次 Normal 模式
 *        该标志上电后清零，首次进入 Normal 后置位，之后不会清零
 */
#define SIT1145_MAIN_STA_NMS        (1U << 5)

/* ==========================================================================
 *  收发器事件使能（0x23）与事件标志（0x24）
 *  两寄存器位定义相同，兼容 TJA1145
 *  事件标志为 W1C（Write-1-to-Clear）：写1清除对应标志
 * ========================================================================== */

/**
 * @brief CWE 位（bit0）：CAN Wake-up Enable
 * @note  写入 TRANSCEIVER_EVENT_EN 寄存器
 *        1 = 使能标准 CAN 唤醒检测（ISO 11898-2 WUP 模式）
 *        0 = 关闭 CAN 唤醒检测
 */
#define SIT1145_CWE                 (1U << 0)

/**
 * @brief WUF 位（bit1）：Wake-Up Frame event
 * @note  读 TRANSCEIVER_EVENT 寄存器
 *        1 = 检测到有效唤醒帧（WUF）
 *        0 = 无 WUF 事件
 *        写1清零（W1C）
 */
#define SIT1145_WUF                 (1U << 1)


/**
 * @brief CW \xb7\xef\xff08bit0\xff09\xff1aCAN Wake-up event
 * @note  TRANSCEIVER_EVENT \xbc\xc4\xb4\xe6\xc6\xf7 bit0\xff0cW1C
 *        1 = \xbc\xec\xb2\xe2\xb5\xbd CAN \xd7\xdc\xcf\xdf\xbb\xbb\xd1\xb3\xc4\xfc\xd5\xd0\xff08WUP\xff09
 *        0 = \xce\xde\xbb\xbb\xd1\xb3\xca\xc2\xbc\xfe
 */
#define SIT1145_CW                  (1U << 0)


/* ==========================================================================
 *  收发器事件状态寄存器位定义（0x63，读/写，W1C）— 数据手册表16
 *  与 0x24(TRANSCEIVER_EVENT) 功能类似但地址不同
 * ========================================================================== */

/**
 * @brief PNFDE 位（bit5）：Partial Network Frame Detection Error
 * @note  局部网络帧检测错误（读/写，W1C）：
 *        0 = 未检测到帧检测错误
 *        1 = 检测到帧检测错误（错误计数器溢出时自动置位）
 */
#define SIT1145_TRX_EVT_STA_PNFDE   (1U << 5)

/**
 * @brief CBS 位（bit4）：CAN Bus Silence
 * @note  CAN 总线静默状态（读/写，W1C）：
 *        0 = CAN 总线有活动
 *        1 = CAN 总线在 tB(silence) 时间内无活动
 */
#define SIT1145_TRX_EVT_STA_CBS     (1U << 4)

/**
 * @brief CF 位（bit1）：CAN Fault
 * @note  CAN 故障事件（读/写，W1C）：
 *        0 = 未检测到 CAN 故障
 *        1 = 检测到 CAN 故障事件
 *        仅在 Normal 模式下 CMC=01 时使能，
 *        TXD 引脚显性或 VCC 欠压时触发
 */
#define SIT1145_TRX_EVT_STA_CF      (1U << 1)

/**
 * @brief CW 位（bit0）：CAN Wake-up
 * @note  CAN 唤醒事件（读/写，W1C）：
 *        0 = 未检测到 CAN 唤醒事件
 *        1 = 检测到 CAN 唤醒事件（标准 WUP 模式）
 */
#define SIT1145_TRX_EVT_STA_CW      (1U << 0)

/* ==========================================================================
 *  唤醒引脚事件状态寄存器位定义（0x64，读/写，W1C）— 数据手册表17
 * ========================================================================== */

/**
 * @brief WPR 位（bit1）：WAKE Pin Rising edge
 * @note  WAKE 引脚上升沿事件（读/写，W1C）：
 *        0 = 未检测到上升沿
 *        1 = 检测到 WAKE 引脚上升沿
 */
#define SIT1145_WAKE_EVT_WPR        (1U << 1)

/**
 * @brief WPF 位（bit0）：WAKE Pin Falling edge
 * @note  WAKE 引脚下降沿事件（读/写，W1C）：
 *        0 = 未检测到下降沿
 *        1 = 检测到 WAKE 引脚下降沿
 */
#define SIT1145_WAKE_EVT_WPF        (1U << 0)
/* ==========================================================================
 *  WUF 帧控制寄存器位定义（0x21，读写）
 *  配置唤醒帧的格式和过滤行为
 * ==========================================================================
 */

/**
 * @brief IDE 位（bit6）：Identifier Extension
 * @note  0 = 标准帧（11 位 ID），1 = 扩展帧（29 位 ID）
 */
#define SIT1145_WUF_IDE             (1U << 6)

/**
 * @brief DLC 位（bit[3:0]）：Data Length Code
 * @note  期望的数据长度（0~8）
 *        DLC=0 时不检查数据掩码，ID 匹配即唤醒
 *        DLC≠0 时需数据字段也匹配
 */
#define SIT1145_WUF_DLC_MASK        0x0FU

/**
 * @brief PNDM 位（bit7）：Partial Networking Data Mask
 * @note  0 = 仅评估 ID + CRC（忽略数据字段）
 *        1 = 评估 ID + CRC + DLC + 数据字段（默认）
 */
#define SIT1145_WUF_PNDM            (1U << 7)

/* ==========================================================================
 *  WUF 配置结构体
 * ==========================================================================
 */

/**
 * @brief  WUF 选择性唤醒配置结构体
 * @note   用于 sit1145_wuf_config() 函数
 */
typedef struct {
  uint8_t  ide;           /**< 0=标准帧(11位ID)，1=扩展帧(29位ID) */
  uint8_t  dlc;           /**< 期望数据长度 0~8，0=不检查数据 */
  uint8_t  pndm;          /**< 0=忽略数据字段，1=评估数据字段 */
  uint32_t can_id;        /**< CAN ID 值 */
  uint32_t can_id_mask;   /**< ID 掩码（1=无关位，0=必须匹配） */
  uint8_t  data_mask[8];  /**< 数据掩码（1=检查该位，0=忽略） */
} sit1145_wuf_cfg_t;

/* ==========================================================================
 *  模式控制寄存器值（0x01）
 *  写入 MODE_CONTROL 寄存器切换工作模式
 *  [2:0] = 模式选择位，其他位保留
 * ========================================================================== */

#define SIT1145_MC_SLEEP_MODE       0x01  /**< Sleep 模式：最低功耗，SPI 不可用 */
#define SIT1145_MC_STANDBY_MODE     0x04  /**< Standby 模式：低功耗监听，SPI 可用 */
#define SIT1145_MC_NORMAL_MODE      0x07  /**< Normal 模式：全功能，CAN 收发正常 */
#define SIT1145_MC_MODE_MASK        0x07  /**< 模式位掩码 [2:0] */

/* ==========================================================================
 *  CAN 控制寄存器位定义（0x20）
 *  配置 CAN 收发器的工作模式和功能使能
 * ========================================================================== */

#define SIT1145_CAN_CTRL_CFDC_POS   6U   /**< bit6：CAN FD 容忍使能（1=容忍 FD 帧） */
#define SIT1145_CAN_CTRL_PNCOK_POS  5U   /**< bit5：局部网络配置有效（0=无效） */
#define SIT1145_CAN_CTRL_CPNC_POS   4U   /**< bit4：选择性唤醒使能（0=关闭） */
#define SIT1145_CAN_CTRL_CMC_POS    0U   /**< bit[1:0]：CAN 收发器模式 */
#define SIT1145_CAN_CTRL_CMC_MASK   0x03 /**< CMC 位掩码 */

/* CAN 控制寄存器配置值（本项目使用的组合） */

#define SIT1145_CAN_FD_TOLERANCE_EN (1U << SIT1145_CAN_CTRL_CFDC_POS)   /**< bit6=1：容忍 CAN FD 帧 */
#define SIT1145_CAN_NETW_INVALID    (0U << SIT1145_CAN_CTRL_PNCOK_POS)  /**< bit5=0：局部网络配置无效 */
#define SIT1145_SEL_WAKEUP_DIS      (0U << SIT1145_CAN_CTRL_CPNC_POS)   /**< bit4=0：关闭选择性唤醒 */

/* CMC 模式值（bit[1:0]）— 数据手册表4 */
#define SIT1145_CMC_OFFLINE         0x00  /**< 离线模式：CAN 收发器完全关闭，不上总线 */
#define SIT1145_CMC_ACTIVE_UVLO     0x01  /**< 主动模式（带欠压检测）：正常收发，VCC 欠压自动恢复 */
#define SIT1145_CMC_ACTIVE_NO_UVLO  0x02  /**< 主动模式（不带欠压检测）：正常收发，VCC 欠压不自动恢复 */
#define SIT1145_CMC_LISTEN_ONLY     0x03  /**< 只听模式：仅接收不发送，用于总线监听/诊断 */

/** @brief 本项目使用的 CMC 模式：主动模式（带欠压检测） */
#define SIT1145_CAN_MODE_NORMAL     SIT1145_CMC_ACTIVE_UVLO

/* ==========================================================================
 *  数据速率寄存器值（0x26）
 *  配置 CAN 总线波特率
 * ========================================================================== */

#define SIT1145_DRATE_50K           0x00  /**< 50 kbps */
#define SIT1145_DRATE_100K          0x01  /**< 100 kbps */
#define SIT1145_DRATE_125K          0x02  /**< 125 kbps */
#define SIT1145_DRATE_250K          0x03  /**< 250 kbps */
#define SIT1145_DRATE_500K          0x05  /**< 500 kbps */
#define SIT1145_DRATE_1000K         0x07  /**< 1000 kbps */

/** @brief 本项目默认 CAN 速率：250kbps */
#define SIT1145_DEFAULT_DRATE       SIT1145_DRATE_250K

/* ==========================================================================
 *  导出函数
 * ========================================================================== */

/**
 * @brief  初始化 SIT1145 CAN 收发器
 * @note   完整初始化流程：
 *         1. 使能 GPIO/SPI 时钟
 *         2. 配置 SPI1 引脚（PA5=SCK, PA6=MISO, PA7=MOSI）
 *         3. 配置 CS 引脚（PA4，默认高电平=未选中）
 *         4. 配置悬空引脚 PB3（输出低）
 *         5. 初始化 SPI1（Mode 1, 8bit, MSB, 软件 CS）
 *         6. 读识别码 0x7E 验证 SPI 通信
 *         7. 配置 CAN Control（CFDC=1, CMC=01）
 *         8. 配置 Data Rate（250kbps）
 *         9. 使能标准 CAN 唤醒（CWE=1）
 *        10. 进入 Standby 模式（APP 低功耗默认状态）
 * @retval 1 成功，0 失败
 */
uint8_t sit1145_init(void);

/**
 * @brief  通过 SPI 写 SIT1145 寄存器
 * @note   SPI 帧：CS↓ → [addr<<1|0] → [data] → CS↑
 * @param  addr: 寄存器地址（7 位）
 * @param  data: 写入值（8 位）
 */
void sit1145_write_reg(uint8_t addr, uint8_t data);

/**
 * @brief  通过 SPI 读 SIT1145 寄存器
 * @note   SPI 帧：CS↓ → [addr<<1|1] → [0xFF, 收 data] → CS↑
 * @param  addr: 寄存器地址（7 位）
 * @retval 寄存器值（8 位），超时返回 0xFF
 */
uint8_t sit1145_read_reg(uint8_t addr);

/**
 * @brief  切换到 Normal 模式并等待 CTS 就绪
 * @note   Normal 模式下 CAN 收发器激活，可收发帧。
 *         切换后轮询 TRANSCEIVER_STATUS 的 CTS 位（最多 20ms），
 *         CTS=1 表示收发器已就绪可驱动总线。
 *         如果第一次切换失败会重试一次。
 * @retval 1 成功（CTS=1），0 失败（超时或 SPI 错误）
 */
uint8_t sit1145_normal_mode_set(void);

/**
 * @brief  切换到 Standby 模式
 * @note   Standby 模式：低功耗监听状态。
 *         - CAN 总线被动（不发送帧）
 *         - SPI 接口仍可访问（可读写寄存器）
 *         - 收发器监听 CAN 总线，检测到 WUP 模式后置位 CW 标志
 *         - RXD 引脚在唤醒事件期间被强制拉低
 * @retval 1 成功，0 失败
 */
uint8_t sit1145_standby_mode_set(void);

/**
 * @brief  切换到 Sleep 模式
 * @note   Sleep 模式：最低功耗。
 *         ⚠️ SPI 接口在 Sleep 模式下完全不可用！
 *         调用后任何 SPI 读写都会超时返回 0xFF。
 *         唤醒方式：INH 引脚电平变化，或重新上电。
 *         调用前须确保 CAN 控制器已停止。
 * @retval 1 始终返回 1（无法回读验证，SPI 已断开）
 */
uint8_t sit1145_sleep_mode_set(void);

/**
 * @brief  获取 SIT1145 当前工作模式
 * @note   读取 MODE_CONTROL 寄存器 [2:0] 位
 * @retval 模式值：
 *         SIT1145_MC_SLEEP_MODE   (0x01) — Sleep
 *         SIT1145_MC_STANDBY_MODE (0x04) — Standby
 *         SIT1145_MC_NORMAL_MODE  (0x07) — Normal
 *         0xFF — SPI 读取失败（可能处于 Sleep 模式）
 */
uint8_t sit1145_get_mode(void);

/**
 * @brief  读取 SIT1145 主状态寄存器（0x03，只读）
 * @note   反映芯片全局状态：
 *         - FSMS（bit7）：Sleep 触发原因（0=SPI 指令，1=VCC 欠压）
 *         - OTWS（bit6）：过温警告（0=正常，1=温度过高）
 *         - NMS（bit5）：是否进入过 Normal（0=从未，1=至少一次）
 * @retval 主状态寄存器原始值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_main_status(void);

/**
 * @brief  判断 Sleep 模式是否由 VCC 欠压触发
 * @note   读取主状态寄存器 FSMS 位（bit7）
 * @retval 1=由欠压触发，0=由 SPI 指令触发，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_sleep_by_undervoltage(void);

/**
 * @brief  查询过温警告状态
 * @note   读取主状态寄存器 OTWS 位（bit6），温度超阈值时置位
 * @retval 1=过温警告，0=温度正常，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_overtemp_warning(void);

/**
 * @brief  查询是否已进入过 Normal 模式
 * @note   读取主状态寄存器 NMS 位（bit5）
 *         上电后首次进 Normal 时硬件置位，之后不清零
 * @retval 1=已进入过 Normal，0=从未进入，0xFF=SPI 读取失败
 */
uint8_t sit1145_has_entered_normal(void);

/**
 * @brief  读取 CAN 控制寄存器（0x20，读写）
 * @note   返回 CAN 控制寄存器原始值，包含 CFDC/PNCOK/CPNC/CMC 等配置位
 * @retval 寄存器原始值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_can_control(void);

/**
 * @brief  获取当前 CMC 模式值（bit[1:0]）
 * @note   读取 CAN 控制寄存器并提取 CMC 位：
 *         - SIT1145_CMC_OFFLINE    (0x00) 离线模式
 *         - SIT1145_CMC_ACTIVE_UVLO(0x01) 主动模式（带欠压检测）
 *         - SIT1145_CMC_ACTIVE_NO_UVLO(0x02) 主动模式（不带欠压检测）
 *         - SIT1145_CMC_LISTEN_ONLY(0x03) 只听模式
 * @retval CMC 模式值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_cmc_mode(void);

/**
 * @brief  读取收发器状态寄存器（0x22，只读）
 * @note   返回收发器状态原始值，包含 CTS/CPNERR 等状态位
 * @retval 寄存器原始值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_transceiver_status(void);

/**
 * @brief  查询 CTS 状态（收发器是否就绪）
 * @note   读取收发器状态寄存器 CTS 位（bit7）
 *         CTS=1 表示收发器已进入激活模式，可驱动 CAN 总线
 * @retval 1=就绪可发送，0=未就绪，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_cts_ready(void);

/**
 * @brief  查询局部网络错误状态
 * @note   读取收发器状态寄存器 CPNERR 位（bit6）
 *         本项目未使用局部网络功能，该位通常为 0
 * @retval 1=有错误，0=无错误，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_cpn_error(void);

/**
 * @brief  查询局部网络同步状态
 * @note   读取收发器状态寄存器 CPNS 位（bit5）
 *         本项目未使用局部网络功能，该位通常为 0
 * @retval 1=已同步，0=未同步，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_cpns_synced(void);

/**
 * @brief  查询 CAN 振荡器状态
 * @note   读取收发器状态寄存器 COSCS 位（bit4）
 * @retval 1=振荡器就绪，0=未就绪，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_osc_ready(void);

/**
 * @brief  查询 CAN 总线静默状态
 * @note   读取收发器状态寄存器 CBSS 位（bit3）
 *         总线静默表示无显性位活动
 * @retval 1=总线静默，0=总线有活动，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_bus_silent(void);

/**
 * @brief  查询 VCC 电源电压状态
 * @note   读取收发器状态寄存器 VCS 位（bit1）
 *         0 = VCC 电压低于欠压阈值
 *         1 = VCC 电压正常
 * @retval 1=电压正常，0=欠压，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_vcc_ok(void);

/**
 * @brief  查询 CAN 故障状态
 * @note   读取收发器状态寄存器 CFS 位（bit0）
 * @retval 1=有故障，0=无故障，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_can_fault(void);

/**
 * @brief  配置选择性唤醒帧（WUF）
 * @note   配置 SIT1145 的 WUF 寄存器，使能 CPNC=1 + PNCOK=1
 *         配置前须确保 CAN Control 和 Data Rate 已设置
 *         配置完成后自动使能 CWE=1
 * @param  cfg: WUF 配置结构体指针
 * @retval 1=配置成功，0=配置失败（回读不匹配）
 */
uint8_t sit1145_wuf_config(const sit1145_wuf_cfg_t *cfg);

/**
 * @brief  使能选择性唤醒（CPNC=1 + PNCOK=1 + CWE=1）
 * @note   必须先调用 sit1145_wuf_config() 配置好 ID/掩码/数据
 *         否则 PNCOK=1 后未配置的寄存器可能导致误唤醒
 */
void sit1145_wuf_enable(void);

/**
 * @brief  关闭选择性唤醒（CPNC=0），恢复标准唤醒模式
 * @note   CWE 保持不变，仍可被标准 WUP 模式唤醒
 */
void sit1145_wuf_disable(void);

/**
 * @brief  读取全局事件状态寄存器（0x60，只读）
 * @note   返回全局事件状态原始值，包含 WPE/TRXE/SYSE 等挂起标志
 * @retval 寄存器原始值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_global_event_status(void);

/**
 * @brief  查询 WAKE 引脚事件是否挂起
 * @note   读取全局事件状态寄存器 WPE 位（bit3）
 * @retval 1=有事件挂起，0=无，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_wake_pin_event(void);

/**
 * @brief  查询收发器事件是否挂起
 * @note   读取全局事件状态寄存器 TRXE 位（bit2）
 *         收发器事件包括 CW/WUF 等唤醒事件
 * @retval 1=有事件挂起，0=无，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_trx_event_pending(void);

/**
 * @brief  查询系统事件是否挂起
 * @note   读取全局事件状态寄存器 SYSE 位（bit0）
 *         系统事件包括 PO/OTW/SPIF 等
 * @retval 1=有事件挂起，0=无，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_sys_event_pending(void);

/**
 * @brief  读取系统事件状态寄存器（0x61，只读）
 * @note   返回系统事件状态原始值，包含 PO/OTW/SPIF 等状态位
 * @retval 寄存器原始值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_system_event_status(void);

/**
 * @brief  查询上电状态
 * @note   读取系统事件状态寄存器 PO 位（bit4）
 *         1 = 电池上电后首次退出关闭模式
 *         读取后硬件自动清零
 * @retval 1=首次上电，0=非首次，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_power_on(void);

/**
 * @brief  查询系统级过温警告
 * @note   读取系统事件状态寄存器 OTW 位（bit2）
 * @retval 1=过温，0=正常，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_sys_overtemp(void);

/**
 * @brief  查询 SPI 故障状态
 * @note   读取系统事件状态寄存器 SPIF 位（bit1）
 * @retval 1=有 SPI 故障，0=正常，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_spi_fault(void);

/**
 * @brief  读取收发器事件状态寄存器（0x63，读/写，W1C）
 * @note   返回收发器事件状态原始值，包含 PNFDE/CBS/CF/CW
 * @retval 寄存器原始值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_trx_event_status(void);

/**
 * @brief  查询局部网络帧检测错误
 * @note   读取收发器事件状态寄存器 PNFDE 位（bit5）
 *         W1C 标志，写1清零
 * @retval 1=有错误，0=无错误，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_pnf_error(void);

/**
 * @brief  查询 CAN 总线静默状态
 * @note   读取收发器事件状态寄存器 CBS 位（bit4）
 *         总线在 tB(silence) 时间内无活动时置位
 * @retval 1=总线静默，0=总线有活动，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_bus_silence(void);

/**
 * @brief  查询 CAN 故障事件
 * @note   读取收发器事件状态寄存器 CF 位（bit1）
 *         仅在 Normal 模式 CMC=01 时使能，TXD 显性或 VCC 欠压时触发
 * @retval 1=有故障事件，0=无故障，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_cf_event(void);

/**
 * @brief  查询 CAN 唤醒事件
 * @note   读取收发器事件状态寄存器 CW 位（bit0），W1C 标志
 * @retval 1=有唤醒事件，0=无唤醒，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_cw_event(void);

/**
 * @brief  读取唤醒引脚事件状态寄存器（0x64，读/写，W1C）
 * @note   返回唤醒引脚事件状态原始值，包含 WPR/WPF
 * @retval 寄存器原始值，SPI 失败返回 0xFF
 */
uint8_t sit1145_get_wake_pin_event_status(void);

/**
 * @brief  查询 WAKE 引脚上升沿事件
 * @note   读取唤醒引脚事件状态寄存器 WPR 位（bit1），W1C 标志
 * @retval 1=检测到上升沿，0=无，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_wake_rising(void);

/**
 * @brief  查询 WAKE 引脚下降沿事件
 * @note   读取唤醒引脚事件状态寄存器 WPF 位（bit0），W1C 标志
 * @retval 1=检测到下降沿，0=无，0xFF=SPI 读取失败
 */
uint8_t sit1145_is_wake_falling(void);


/**
 * @brief  使能标准 CAN 唤醒（写 CWE=1 到 EVENT_EN 寄存器）
 * @note   使能后 SIT1145 在 Standby 模式下监听 CAN 总线，
 *         检测到 ISO 11898-2 WUP 模式后置位 CW 标志。
 *         同时关闭选择性唤醒（WUF），只用标准唤醒。
 */
void sit1145_wake_enable(void);

/**
 * @brief  检测是否有 CAN 唤醒事件挂起
 * @note   双重检测机制：
 *         1. 先查 PA11 引脚电平 — Standby 时 SIT1145 在整个唤醒事件期间
 *            强制 RXD(PA11) 拉低，MCU 可通过 GPIO 读到低电平
 *         2. 再查 SPI 寄存器 — 读 TRANSCEIVER_EVENT 的 CW 位
 *            （CW=1 表示检测到 WUP 模式，W1C 标志）
 *         PA11 引脚检测更快（无 SPI 开销），CW 寄存器更可靠
 * @retval 0=无；1=PA11 RXD 低；2=0x24 CW/WUF；3=0x63 CW
 */
uint8_t sit1145_wakeup_pending(void);

/**
 * @brief  清除 CAN 唤醒事件标志（CW + WUF，写1清零）
 * @note   向 TRANSCEIVER_EVENT 寄存器的 CW 和 WUF 位写1，
 *         硬件自动清零（W1C 机制）。
 *         进入 Standby 前和唤醒后都应调用此函数。
 */
void sit1145_wakeup_clear(void);

/**
 * @brief  清除 WUF 唤醒帧事件标志（写1清零）
 * @note   仅清除 WUF 位，不影响 CW 位
 *         用于选择性唤醒场景下单独清零
 */
void sit1145_wuf_clear(void);

#ifdef __cplusplus
}
#endif

#endif /* __SIT1145_H */
