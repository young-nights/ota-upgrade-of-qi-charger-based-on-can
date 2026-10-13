  /******************************************************************************
  * @file    SIT1145.h
  * @author  FAE Team
  * @version V1.0.2bat
  * @date    10-5-2023
  * @created by jalen deng
  * @brief   This file is the SIT1145 driver
  ******************************************************************************/
#ifndef SIT1145
#define SIT1145

#include "stm32f10x.h"

//配制片选引脚
#define SIT1145_SPI_GPIO     GPIOB  
#define SIT1145_CS_PIN       GPIO_Pin_13

#define  SIT1145_CS_SET()      GPIO_SetBits(SIT1145_SPI_GPIO,SIT1145_CS_PIN) // SIT1145_SPI_GPIO->BSRR |= SIT1145_CS_PIN
#define  SIT1145_CS_RESET()  GPIO_ResetBits(SIT1145_SPI_GPIO,SIT1145_CS_PIN) //     SIT1145_SPI_GPIO->BRR |= SIT1145_CS_PIN

//配制帧数据
#define  _FRAME_IDE      _STANDARD //帧格式 _STANDARD  _EXTENDED    
#define  _PNDM                FRAME_CTRL_PNDM_EN  //  FRAME_CTRL_PNDM_DIS  FRAME_CTRL_PNDM_EN   部分网络数据掩码（0：只计算标识符，数据场忽略、1：数据字段包含在唤醒帧中）
#define  _DLC                   (0x2)           //数据长度
#define  _DRATE_CDR       DRATE_500k //速率 #define  DRATE_50k  DRATE_100k   DRATE_125k  DRATE_250k DRATE_500k  DRATE_1000k
#define  _ID                      (u32)0x00000200   //(11BIT OR 29BIT)  ID
#define  _ID_MASK           (u32)0x00000001    //(11BIT OR 29BIT) IDMASK
#define  _DATA0_MASK    (u8)0xFF   //
#define  _DATA1_MASK    (u8)0xFF   //
#define  _DATA2_MASK    (u8)0xFF   //
#define  _DATA3_MASK    (u8)0xFF   //
#define  _DATA4_MASK    (u8)0xFF   //
#define  _DATA5_MASK    (u8)0xFF   //
#define  _DATA6_MASK    (u8)0xFF   //
#define  _DATA7_MASK    (u8)0xFF   //

/*
//配制唤醒条件
#define  WEAK_PIN_EVEN                 WAKE_PIN_CHANGE        // 本地唤醒   WAKE_PIN_FALLING    WAKE_PIN_CHANGE   WAKE_PIN_CLOSE
#define  CAN_BUS_SILENCE_CONF    CAN_BUS_SILENCE_DIS //CAN总线静默CAN_BUS_SILENCE_EN    CAN_BUS_SILENCE_DIS
#define  CAN_FAILURE_CONF            CAN_FAILURE_EN           //CAN总线故障CAN_FAILURE_EN     CAN_FAILURE_DIS
#define  REMOTE_WEAKUP_CONF      REMOTE_WEAKUP_EN     //远程唤醒     REMOTE_WEAKUP_EN    REMOTE_WEAKUP_DIS 
//
*/

//配制唤醒条件
#define  WEAK_PIN_EVEN                 WAKE_PIN_CHANGE        // 本地唤醒   WAKE_PIN_FALLING    WAKE_PIN_CHANGE   WAKE_PIN_CLOSE
#define  CAN_BUS_SILENCE_CONF    CAN_BUS_SILENCE_DIS //CAN总线静默CAN_BUS_SILENCE_EN    CAN_BUS_SILENCE_DIS
#define  CAN_FAILURE_CONF            CAN_FAILURE_EN           //CAN总线故障CAN_FAILURE_EN     CAN_FAILURE_DIS
#define  REMOTE_WEAKUP_CONF      REMOTE_WEAKUP_DIS    //远程唤醒     REMOTE_WEAKUP_EN    REMOTE_WEAKUP_DIS 
//


/****************************************************************************************************/
/*                                                                                                                                                                           */
/****************************************************************************************************/
//寄存器定义
///////////////////////////////////////////////////////////////////////////////////////////////
#define  SIT1145_Mode_control   (u8)0x01
#define  MC_SLEEP_MODE            (0x01) //睡眠模式
#define  MC_STANDBY_MODE       (0x04) //待机模式
#define  MC_NORMAL_MODE         (0x07) // 正常模式
#define  MC_MODE_WEN               (0x07) // 正常模式

/********************************************************/
#define  SIT1145_Main_status   (u8)0x03   // only Read
#define  MAIN_STA_FSMS_POS       (7U) //FSMS：睡眠模式转换状态（0：转换到睡眠模式由SPI命令、1：VCC或VIO欠压事件强制） 
#define  MAIN_STA_OTWS_POS      (6U) // OTWS：过温警告状态（0：芯片温度低于过温警告阈值、1：芯片温度高于警告阈值）        
#define  MAIN_STA_NMS_POS         (5U) // NMS：正常模式状态（0：上电后已进入正常模式、1：已上电，但仍没有进入正常模式）
/********************************************************/
#define SIT1145_System_event_enable   (u8)0x04
#define SYS_EVE_ENB_OTWE_POS    (2U) //OTWE：过温警告使能（0：禁用、1：使能）
#define SYS_EVE_ENB_SPIFE_POS    (1U) //SPIFE：SPI故障使能（0：禁用、1：使能）
/********************************************************/
//用于存储用户信息的通用寄存器分配4个字节的内存。
//通用寄存器可以通过地址0x06到0x09的SPI访问
#define  SIT1145_Memory_0   (u8)0x06
#define  SIT1145_Memory_1   (u8)0x07
#define  SIT1145_Memory_2   (u8)0x08
#define  SIT1145_Memory_3   (u8)0x09
/********************************************************/
#define  SIT1145_Lock_control   (u8)0x0A
#define  LOCK_CTRL_LK6C_POS    (6U) //LK6C ：锁住部分网络数据字节掩码寄存器，地址0x68-0x6F（0：SPI写使能、1：SPI写禁用）
#define  LOCK_CTRL_LK5C_POS    (5U) //LK5C ：锁住地址0x50-0x5F（0：SPI写使能、1：SPI写禁用）
#define  LOCK_CTRL_LK4C_POS    (4U) //LK4C ：锁住WAKE引脚配置寄存器，地址0x40-0x4F（0：SPI写使能、1：SPI写禁用）
#define  LOCK_CTRL_LK3C_POS    (3U) //LK3C ：锁住地址0x30-0x3F（0：SPI写使能、1：SPI写禁用）
#define  LOCK_CTRL_LK2C_POS    (2U) //LK2C ：锁住部分网络和收发器控制，地址0x20-0x2F（0：SPI写使能、1：SPI写禁用）
#define  LOCK_CTRL_LK1C_POS    (1U) //LK1C ：锁住地址0x10-0x1F（0：SPI写使能、1：SPI写禁用）
#define  LOCK_CTRL_LK0C_POS    (0U) //LK0C ：锁住通用寄存器，地址0x06-0x09（0：SPI写使能、1：SPI写禁用）
/********************************************************/
//收发器控制和部分网络寄存器
#define  SIT1145_CAN_control   (u8)0x20
#define  CAN_CTRL_CFDC_POS      (6U) //CFDC：CAN FD容许（0：CAN FD禁用、1：使能）  
#define  CAN_CTRL_PNCOK_POS     (5U) //PNCOK：CAN部分网络配置（0：无效、1：成功）            
#define  CAN_CTRL_CPNC_POS      (4U) //CPNC：选择性唤醒（0：禁用、1：使能）  
#define  CAN_CTRL_CMC_POS        (0U) ////CMC：CAN收发器操作模式选择（00：离线模式、01：主动模式（VCC90%欠压检测激活）、10：主动模式（VCC欠压检测无效））、11：只听模式     
#define  CAN_CTRL_CMC_MSK        (3U<<CAN_CTRL_CMC_POS)

#define CAN_FD_TOLERANCE_DIS            (0<<CAN_CTRL_CFDC_POS)    //CAN FD容许禁用
#define CAN_FD_TOLERANCE_EN             (1<<CAN_CTRL_CFDC_POS)    // CAN FD容许使能
#define CAN_NETW_CONF_INVALID         (0<<CAN_CTRL_PNCOK_POS)  // CAN部分网络配置（0：无效）            
#define CAN_NETW_CONF_SUCCESSFUL  (1<<CAN_CTRL_PNCOK_POS)  // CAN部分网络配置（1：成功）  
#define SEL_WAKEUP_DIS                        (0<<CAN_CTRL_CPNC_POS)   // 选择性唤醒禁用
#define SEL_WAKEUP_EN                         (1<<CAN_CTRL_CPNC_POS)    //选择性唤醒使能

#define  CAN_OFFLINE_MODE                (0x00)  //离线模式
#define  CAN_EN_LPC_ACTIVE_MODE    (0x01)  //主动模式 欠压激活
#define  CAN_DIS_LPC_ACTIVE_MODE   (0x02)   //主动模式 欠压无效
#define  CAN_LISTEN_MODE                   (0x03)   //只听模式 
/********************************************************/
#define  SIT1145_Transceiver_status   (u8)0x22   // only read
#define  TRAN_STA_CTS_POS          (7U) //CTS：CAN收发器状态（0：CAN收发器未工作在主动模式、1：CAN收发器工作在主动模式）显性超时时置“0”
#define  TRAN_STA_CPNERR_POS      (6U) //CPNERR：CAN部分网络错误状态（0：CAN部分网络未检测到错误（PNFDE=0 AND PNCOK=1没有帧错误和部分网络配置正确）、1：CAN部分网络检测到错误（PNFDE=1 OR PNCOK=0有帧错误和部分网络配置不成功））
#define  TRAN_STA_CPNS_POS        (5U) //CPNS：部分网络状态（0：部分网络配置错误（PNCOK=0）、1：部分网络配置成功（PNCOK=1）
#define  TRAN_STA_COSCS_POS       (4U) //COSCS：CAN振荡器状态（0：部分网络振荡器没有按目标频率运行、1：部分网络按目标频率运行）
#define  TRAN_STA_CBSS_POS        (3U) //CBSS：CAN总线静默状态（0：CAN总线活动（在CAN总线上检测到通信）、1：CAN总线不活动（超过静默时间））
#define  TRAN_STA_VCS_POS          (1U) //VCS：VCC供电状态（0：VCC高于欠压检测阈值、1：VCC低于欠压检测阈值）
#define  TRAN_STA_CFS_POS          (0U) //CFS：CAN总线故障（0：TXD没有检测到显性超时、1：由于TXD显性超时，CAN收发器禁用）
/********************************************************/
#define  SIT1145_Transceiver_event_enable   (u8)0x23
#define  TRAN_EVE_ENB_CBSE_POS         (4U) //CBSE：总线静默使能（0：总线静默检测禁用、1：总线静默检测使能）
#define  TRAN_EVE_ENB_CFE_POS           (1U) //CFE：CAN总线故障使能 （0：CAN故障检测禁用、1：CAN故障检测使能） 
#define  TRAN_EVE_ENB_CWE_POS          (0U) //CWE：CAN唤醒使能 （0：CAN唤醒检测禁用、1：CAN唤醒检测使能）
#define  CAN_BUS_SILENCE_EN          (_EN  <<TRAN_EVE_ENB_CBSE_POS)    //CAN总线静默使能
#define  CAN_BUS_SILENCE_DIS         (_DIS <<TRAN_EVE_ENB_CBSE_POS)   //CAN总线静默禁用
#define  CAN_FAILURE_EN              (_EN  <<TRAN_EVE_ENB_CFE_POS)      //CAN总线故障使能
#define  CAN_FAILURE_DIS             (_DIS <<TRAN_EVE_ENB_CFE_POS)      //CAN总线故障禁用
#define  REMOTE_WEAKUP_EN            (_EN  <<TRAN_EVE_ENB_CWE_POS)     //远程唤醒使能
#define  REMOTE_WEAKUP_DIS           (_DIS <<TRAN_EVE_ENB_CWE_POS)     //远程唤醒关闭
#define  TRAN_EVE_WEN                    (CAN_BUS_SILENCE_EN | CAN_FAILURE_EN  | REMOTE_WEAKUP_EN) 
/********************************************************/
#define  SIT1145_Data_rate   (u8)0x26
#define  DRATE_CDR_POS          (0U)
//CDR：CAN数据速率选择
//000 50kbit/s、0
//01 100k、
//010 125k、
//011 250k、
//100保留目前选择500k、
//101 500k、
//110 保留目前选择500k、
//111 1000k
#define  DRATE_50k       (0X00)
#define  DRATE_100k      (0X01)
#define  DRATE_125k      (0X02)
#define  DRATE_250k      (0X03)
#define  DRATE_500k      (0X05)
#define  DRATE_1000k     (0X07)
/********************************************************/
#define  SIT1145_Identifier_0   (u8)0x27    //扩展帧格式ID7到ID0位
#define  SIT1145_Identifier_1   (u8)0x28   //扩展帧格式ID15到ID8位
#define  SIT1145_Identifier_2   (u8)0x29   //扩展帧格式ID23到ID16位/ID[23:18] 标准帧ID5到ID0
#define  SIT1145_Identifier_3   (u8)0x2A   //扩展帧格式ID28到ID24位/标准帧ID10到ID6
#define  SIT1145_IdMask_0   (u8)0x2B   //掩码位扩展帧格式ID7到ID0位
#define  SIT1145_IdMask_1   (u8)0x2C   //掩码位扩展帧格式ID15到ID8位
#define  SIT1145_IdMask_2   (u8)0x2D   //掩码位扩展帧格式ID23到ID16位/ID[23:18] 标准帧ID5到ID0
#define  SIT1145_IdMask_3   (u8)0x2E   //掩码位扩展帧格式ID28到ID24位/标准帧ID10到ID6
/********************************************************/
#define  SIT1145_Frame_control   (u8)0x2F
#define  FRAME_CTRL_IDE_POS        (7U)   //IDE：标识符格式（0：标准帧格式11bit、1：扩展帧格式29bit）
#define  FRAME_CTRL_PNDM_POS     (6U)   //PNDM：部分网络数据掩码（0：只计算标识符，数据场忽略、1：数据字段包含在唤醒帧中）
#define  FRAME_CTRL_DLC_POS        (0U)   //DLC:CAN帧中数据字节数（0000 数据长度0~1000 数据长度8、1001~1111容忍8字节以上）
#define  FRAME_STANDARD               (0<<FRAME_CTRL_IDE_POS)
#define  FRAME_EXTENDED                (1<<FRAME_CTRL_IDE_POS)
#define  FRAME_CTRL_PNDM_EN       (_EN<<FRAME_CTRL_PNDM_POS)
#define  FRAME_CTRL_PNDM_DIS      (_DIS<<FRAME_CTRL_PNDM_POS)

/********************************************************/
#define  SIT1145_Data_mask_0   (u8)0x68 //数据掩码0配置DLC=8/DLC＞8忽略
#define  SIT1145_Data_mask_1   (u8)0x69 //数据掩码1配置
#define  SIT1145_Data_mask_2   (u8)0x6A //数据掩码2配置
#define  SIT1145_Data_mask_3   (u8)0x6B //数据掩码3配置
#define  SIT1145_Data_mask_4   (u8)0x6C //数据掩码4配置
#define  SIT1145_Data_mask_5   (u8)0x6D //数据掩码5配置
#define  SIT1145_Data_mask_6   (u8)0x6E //数据掩码6配置
#define  SIT1145_Data_mask_7   (u8)0x6F //数据掩码7配置DLC=1
/********************************************************/
//WAKE控制和状态寄存器
#define  SIT1145_WAKE_status   (u8)0x4B  //only read
#define  WAKE_STA_WPVS_POS        (1U)   //WPVS：唤醒引脚状态（0：本地唤醒引脚上电压低于开关阈值电压、1：本地唤醒引脚上电压高于开关阈值电压）
/********************************************************/
#define  SIT1145_WAKE_enable   (u8)0x4C
#define  WAKE_WPRE_EN_POS        (1U)   //WPRE：本地唤醒引脚上的上升沿使能（0：禁用、1：使能）
#define  WAKE_WPFE_EN_POS        (0U)   //WPFE：本地唤醒引脚上的下降沿使能（0：禁用、1：使能）
#define  WAKE_PIN_RISING             ((1<<WAKE_WPRE_EN_POS)+(0<<WAKE_WPFE_EN_POS))  //上升沿
#define  WAKE_PIN_FALLING          ((0<<WAKE_WPRE_EN_POS)+(1<<WAKE_WPFE_EN_POS))  //下降沿
#define  WAKE_PIN_CHANGE            ((1<<WAKE_WPRE_EN_POS)+(1<<WAKE_WPFE_EN_POS))  //电平变化
#define  WAKE_PIN_CLOSE             (0)

#define  WAKE_PIN_EN_WEN           (3U)
/*
//唤醒方式设置
#define WAKE_RISING    1
#define WEAK_FALLING  2
#define WAKE_CHANGE   3
#define TRAN_CWE         4
*/
/********************************************************/
//事件捕获寄存器
#define  SIT1145_Global_Event_capture_status   (u8)0x60//only read
#define  GLOBAL_EVE_STA_WPE_POS   (3U)  //WPE：本地唤醒事件（0：没有挂起的本地唤醒事件、1：本地唤醒事件挂起在地址0x64）
#define  GLOBAL_EVE_STA_WPE_SET   (1U<<GLOBAL_EVE_STA_WPE_POS)  //  1：本地唤醒事件挂起在地址

#define  GLOBAL_EVE_STA_TRXE_POS  (2U) //TRXE:收发器事件（0：没有挂起的收发器事件、1：收发器挂起事件在地址0x63）
#define  GLOBAL_EVE_STA_SYSE_POS  (0U) //SYSE：系统事件（0：没有挂起的系统事件、1：系统事件挂起在地址0x61）

/********************************************************/
#define  SIT1145_System_event_status   (u8)0x61   //
#define  SYS_EVE_STA_PO_POS        (4U) //PO：上电（0：最近电池没有上电、1：在电池上电之后离开关机模式）
#define  SYS_EVE_STA_OTW_POS     (2U) //OTW：过温警告（0：过温没有被检测、1：整个芯片温度超过过温警告阈值）
#define  SYS_EVE_STA_SPIF_POS     (1U) //SPIF：SPIF故障（0:没有检测SPI故障、1：检测SPI故障）
/********************************************************/
#define  SIT1145_Transceiver_event_status   (u8)0x63
#define  TRAN_EVE_STA_PNFDE_POS    (5U) //PNFDE：部分网络帧检测错误（0：无、1：有错误）
#define  TRAN_EVE_STA_CSB_POS         (4U) //CBS：CAN总线状态：在CAN所有模式，总线活动时，检测到隐性时间持续一个静默时间（0：CAN总线有活动、1：CAN总线没有活动）                                                                                                    
#define  TRAN_EVE_STA_CF_POS           (1U) //CF：CAN故障（0：无、1：有故障被检测） 
#define  TRAN_EVE_STA_CW_POS          (0U) //CW：CAN唤醒（0：没有CAN唤醒事件被检测、1：有）  
/********************************************************/
#define  SIT1145_Wake_event   (u8)0x64
#define  WAKE_EVE_WPR_POS       (1U) //WPR：WAKE上升沿检测（0：无、1：有）
#define  WAKE_EVE_WPF_POS       (0U) //WPF：WAKE下降沿检测（0：无、1：有）

/********************************************************/
// 产品标识寄存器
#define  SIT1145_Identification   (u8)0x7E

///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
#define _WRITE  (0)
#define _READ    (1)

#define _DIS       (0)
#define _EN        (1)

//CAN 帧格式
#define _STANDARD   (0)
#define _EXTENDED    (1)

void SIT1145_WriteReg(u8 *pbuf,u8 sendLen);
void SIT1145_ReadReg(u8 *pbuf,u8 cmd,u8 recLen);

void  SIT1145_ShowReg(void);
void  SIT1145_RegReset(void);
u8 SIT1145_ReadStatus(void);

u8 SIT1145_ID_Set(u32 ID,u8 form);
u8 SIT1145_IDMask_Set(u32 IDmask,u8 form);
u8 SIT1145_WakeUpFrame_Set(void);
u8 SIT1145_SleepMode_Set(void);
u8 SIT1145_StandbyMode_Set(void) ;
u8  SIT1145_CANCtrl_Set(u8 CFCD, u8 PNCOK, u8 CPNC, u8 CMC);
u8 SIT1145_NormalMode_Set(void);
void SIT1145_SPICS_Init(void);
void SIT1145_Init(void);

#endif 
