  /******************************************************************************
  * @file    SIT1145.c
  * @author  FAE Team
  * @version V1.0.3beta
  * @date    11-7-2023
  * @created by jalen deng
  * @brief   This file is the SIT1145 driver
  ******************************************************************************/
#include "SIT1145.h"
#include "IO_SPI.h"
#include "stm32f10x.h"
#include "usart.h"
#include "delay.h"
#include "usart.h"

static const  u8 c_SIT1145_RegAddr[]     ={ 0x01,0x04,0x06,0x07,0x08,0x09,0x0A,0x20,0x23,0x26,
                                                                    0x27,0x28,0x29,0x2A,0x2B,0x2C,0x2D,0x2E,0x2F,0x68,
                                                                    0x6A,0x6B,0x6C,0x6D,0x6E,0x6F,0x4C,0x61,0x63,
                                                                    0x64,0x7e}; 
static const u8 c_SIT1145_StaRegAddr[] ={ 0x03,0x22,0x4B,0x60};
static const u8 c_SIT1145_RegDefault[]  ={ 0x04,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x05,
                                                                    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x40,0xff,
                                                                    0xff,0xff,0xff,0xff,0xff,0xff,0xff,0x00,0x10,0x00,
                                                                    0x00};

////////////////////////////////////////////////////////////////////////////////
void SIT1145_WriteReg(u8 *pbuf,u8 sendLen)
{
    u8 i;
    SIT1145_CS_RESET();
  //  delay_us(1);
    pbuf[0] =  (pbuf[0]<<1);
    for(i=0;i<sendLen;i++)
    {
        SPI_SendReceiveByte(pbuf[i]);
    }
    SIT1145_CS_SET();
    delay_us(1);
}
////////////////////////////////////////////////////////////////////////////////
void SIT1145_ReadReg(u8 *pbuf,u8 cmd,u8 recLen)
{
    u8 i;
    SIT1145_CS_RESET();
    SPI_SendReceiveByte((cmd<<1)+_READ);
    for(i=0;i<recLen;i++)
    {
        pbuf[i]=SPI_SendReceiveByte(0xff);
    }
    SIT1145_CS_SET();
    delay_us(1);
}


/////////////////////////////////////********************/
void  SIT1145_ShowReg(void)
{
    u8 rbuf[10];

    u8 i;
    printf("\r\n ");
    for(i=0;i<32;i++)
    {
        if((i%8)==0)printf("\r\n ");
        SIT1145_ReadReg(rbuf, c_SIT1145_RegAddr[i], 1);
        printf(" Reg0x%02x=0x%02x ",c_SIT1145_RegAddr[i],rbuf[0]);
    }
    printf("\r\n ");
    for(i=0;i<4;i++)
    {
        SIT1145_ReadReg(rbuf, c_SIT1145_StaRegAddr[i], 1);
        printf(" Reg0x%02x=0x%02x ",c_SIT1145_StaRegAddr[i],rbuf[0]);
    }
}
void  SIT1145_RegReset(void)
{
    u8 sbuf[2];
    u8 i;
    for(i=0;i<32;i++)
    {
        sbuf[0] = c_SIT1145_RegAddr[i];
        sbuf[1] = c_SIT1145_RegDefault[i];
        SIT1145_WriteReg(sbuf,2);
    }
}

u8 SIT1145_ReadStatus(void)
{
    u8 sbuf[10];
    u8 rbuf[10];

   SIT1145_ReadReg(rbuf, SIT1145_Main_status, 1);//读主状态寄存器
    printf("Main_status = %x",rbuf[0]);
    
    SIT1145_ReadReg(rbuf, SIT1145_Transceiver_status, 1);//读收发器控制和部分网络寄存器
    printf("Transceiver_status = %x",rbuf[0]);

    SIT1145_ReadReg(rbuf, SIT1145_Global_Event_capture_status, 1);//读事件捕获状态寄存器
    printf("Event_capture_status = %x",rbuf[0]);
    
    SIT1145_ReadReg(rbuf, SIT1145_System_event_status, 1);//读系统事件状态寄存器
    printf("System_event_status = %x",rbuf[0]);
    
    SIT1145_ReadReg(rbuf, SIT1145_Transceiver_event_status, 1);//读通讯事件状态寄存器
    printf("Transceiver_event_status = %x",rbuf[0]);
    
    SIT1145_ReadReg(rbuf, SIT1145_Wake_event, 1);//读唤醒事件状态寄存器
    printf("\r\n Wake_event=%x  ",rbuf[0]);

    if(rbuf[0]&0x03)
    {
        //清除唤醒事件状态寄存器
        sbuf[0]=SIT1145_Wake_event;
        sbuf[1]=0xff;
        SIT1145_WriteReg(sbuf,2);
        SIT1145_NormalMode_Set();
    }
    return 1;
}
/////////////////////////////////////////////////


///////////////////////////////////////////////////////////////////////////////
u8  SIT1145_CANCtrl_Set(u8 CFCD, u8 PNCOK, u8 CPNC, u8 CMC)
{
    u8 sbuf[10];
    u8 rbuf[10];
        
    //设定CAN 
    sbuf[0]= (SIT1145_CAN_control);
    sbuf[1]= (CFCD |PNCOK |CPNC |CMC);
    SIT1145_WriteReg(sbuf,2);
    SIT1145_ReadReg(rbuf,SIT1145_CAN_control,1);
    if(rbuf[0] != sbuf[1])
    {
        printf("\r\n set CAN_control failed Set%d  read%x", sbuf[1], rbuf[0]);
        return 0;
    }
    return 1;
}
////////////////////////////////////////////////////////////////////////////////
//CAN 帧格式
//#define _STANDARD   0
//#define _EXTENDED   1
u8 SIT1145_ID_Set(u32 ID,u8 format)
{
    u8 sbuf[4];
    u8 rbuf[4];
    u8 i;
    
    if(format == _STANDARD)
    {
        printf("\r\n set _STANDARD");
        sbuf[0]= (SIT1145_Identifier_2);
        sbuf[1]= (u8)((ID<<2)&0xfC);     //ID5-ID0
        sbuf[2]= (u8)((ID>>6)&0x1f);     //ID10-ID6 

        SIT1145_WriteReg(sbuf,3);
        SIT1145_ReadReg(rbuf,SIT1145_Identifier_2,3);

        for(i=0;i<2;i++)
        {
            if(rbuf[i] != sbuf[i+1])
            {
                printf("\r\n set  Id failed ID%d Set %d  read %x",i, sbuf[i+1], rbuf[i]);
                return 0;
            }
        }
    }
    else
    {
        sbuf[0]= (SIT1145_Identifier_0);
        sbuf[1]= (u8)(ID&0xff);            //ID7-ID0
        sbuf[2]= (u8)((ID>>8)&0xff);     //ID15-ID8 
        sbuf[3]= (u8)((ID>>16)&0xff);     //ID23-ID6 
        SIT1145_WriteReg(sbuf,4);
        SIT1145_ReadReg(rbuf,SIT1145_Identifier_0,3);
        for(i=0;i<3;i++)
        {
            if(rbuf[i] != sbuf[i+1])
            {
                printf("\r\n set  Id failed ID%d Set %d  read %x",i, sbuf[i+1], rbuf[i]);
                return 0;
            }
        }
        sbuf[0]= (SIT1145_Identifier_3);
        sbuf[1]= (u8)((ID>>24)&0x1f);            //ID28-ID24
        SIT1145_WriteReg(sbuf,2);
        SIT1145_ReadReg(rbuf,SIT1145_Identifier_3,2);
        if(rbuf[0] != sbuf[1])
        {
            printf("\r\n set  Id failed ID%d Set %d  read %x",i, sbuf[i+1], rbuf[i]);
            return 0;
        }
    }
    return 1;
}
////////////////////////////////////////////////////////////////////////////////

//CAN 帧格式
//#define _STANDARD   0
//#define _EXTENDED   1
u8 SIT1145_IDMask_Set(u32 IDmask,u8 format)
{
    u8 sbuf[4];
    u8 rbuf[4];
    u8 i;
    
    if(format == _STANDARD)
    {
        sbuf[0]= (SIT1145_IdMask_2);
        sbuf[1]= (u8)((IDmask<<2)&0xfC);    // IDM5-ID0
        sbuf[2]= (u8)((IDmask>>6)&0x1f);     //IDM5-ID0  

        SIT1145_WriteReg(sbuf,3);
        SIT1145_ReadReg(rbuf,SIT1145_IdMask_2,3);

        for(i=0;i<2;i++)
        {
            if(rbuf[i] != sbuf[i+1])
            {
                printf("\r\n set  Idmask failed ID%d Set %d  read %x",i, sbuf[i+1], rbuf[i]);
                return 0;
            }
        }
    }
    else
    {
        sbuf[0]= (SIT1145_IdMask_0);
        sbuf[1]= (u8)(IDmask&0xff);            //IDM7-IDM0
        sbuf[2]= (u8)((IDmask>>8)&0xff);     //IDM15-IDM8 
        sbuf[3]= (u8)((IDmask>>16)&0xff);     //IDM23-IDM6 
        SIT1145_WriteReg(sbuf,4);
        SIT1145_ReadReg(rbuf,SIT1145_IdMask_0,3);
        for(i=0;i<3;i++)
        {
            if(rbuf[i] != sbuf[i+1])
            {
                printf("\r\n set  Idmask failed ID%d Set %d  read %x",i, sbuf[i+1], rbuf[i]);
                return 0;
            }
        }
        
        //ID mask set
        sbuf[0]= (SIT1145_IdMask_3);
        sbuf[1]= (u8)((IDmask>>16)&0xff);     //IDM28-IDM24
        SIT1145_WriteReg(sbuf,2);
        SIT1145_ReadReg(rbuf,SIT1145_IdMask_3,1);
        if(rbuf[i] != sbuf[i+1])
        {
            printf("\r\n set  Idmask failed ID%d Set %d  read %x",i, sbuf[i+1], rbuf[i]);
            return 0;
        }
    }
    return 1;
}

////////////////////////////////////////////////////////////////////////////////
u8 SIT1145_WakeUpFrame_Set(void)
{
    u8 sbuf[4];
    u8 rbuf[4];
    u8 i;
    
    SIT1145_ID_Set(_ID,_FRAME_IDE);
    SIT1145_IDMask_Set(_ID_MASK,_FRAME_IDE);
    
    sbuf[0]= (SIT1145_Transceiver_event_enable);
    sbuf[1]= CAN_BUS_SILENCE_DIS |  //CBSE：总线静默使能（0：总线静默检测禁用）
                 CAN_FAILURE_EN|            //CFE：CAN总线故障使能1：CAN故障检测使能） 
                 REMOTE_WEAKUP_EN;        //CWE：CAN唤醒使能
    SIT1145_WriteReg(sbuf,2);
    SIT1145_ReadReg(rbuf,SIT1145_Transceiver_event_enable,1);
    if(rbuf[0] != sbuf[1])
    {
        printf("\r\n set Transceiver_enable failed Set%d  read%x", sbuf[1], rbuf[0]);
        return 0;
    }
    
    sbuf[0]= (SIT1145_Data_rate);
    sbuf[1]= (_DRATE_CDR); //CDR：CAN数据速率选择
    SIT1145_WriteReg(sbuf,2);
    SIT1145_ReadReg(rbuf,SIT1145_Data_rate,1);
    if(rbuf[0] != sbuf[1])
    {
        printf("\r\n set  Data_rate failed Set %d  read %x", sbuf[1], rbuf[0]);
        return 0;
    }
    
  //Data mask set
    sbuf[0]= (SIT1145_Data_mask_0);
    sbuf[1]= _DATA0_MASK;  //Data_mask_0
    sbuf[2]= _DATA1_MASK;  //Data_mask_1
    sbuf[3]= _DATA2_MASK;  //Data_mask_2
    SIT1145_WriteReg(sbuf,4);
    SIT1145_ReadReg(rbuf,SIT1145_Data_mask_0,3);
    for(i=0;i<3;i++)
    {
        if(rbuf[i] != sbuf[i+1])
        {
            printf("\r\n set  Data_mask0 failed Da%d Set %d  read %x",i, sbuf[i+1], rbuf[i]);
            return 0;
        }
    }

    sbuf[0]= (SIT1145_Data_mask_3);
    sbuf[1]= _DATA3_MASK;  //Data_mask_3
    sbuf[2]= _DATA4_MASK;  //Data_mask_4 
    sbuf[3]= _DATA5_MASK;  //Data_mask_5
    SIT1145_WriteReg(sbuf,4);
    SIT1145_ReadReg(rbuf,SIT1145_Data_mask_3,3);
    for(i=0;i<3;i++)
    {
        if(rbuf[i] != sbuf[i+1])
        {
            printf("\r\n set  Data_mask failed Da%d Set %d  read %x",3+i, sbuf[i+1], rbuf[i]);
            return 0;
        }
    }

    sbuf[0]= (SIT1145_Data_mask_6);
    sbuf[1]= _DATA6_MASK;  //Data_mask_6
    sbuf[2]= _DATA7_MASK;  //Data_mask_7
    SIT1145_WriteReg(sbuf,3);
    SIT1145_ReadReg(rbuf,SIT1145_Data_mask_6,2);
    for(i=0;i<2;i++)
    {
        if(rbuf[i] != sbuf[i+1])
        {
            printf("\r\n set  Data_mask failed Da%d Set %d  read %x",3+i, sbuf[i+1], rbuf[i]);
            return 0;
        }
    }

    //帧格式
    sbuf[0]= (SIT1145_Frame_control);
    sbuf[1]= (_FRAME_IDE<<FRAME_CTRL_IDE_POS) | _PNDM | _DLC ; //
    SIT1145_WriteReg(sbuf,2);
    SIT1145_ReadReg(rbuf,SIT1145_Frame_control,1);
    if(rbuf[0] != sbuf[1])
    {
        printf("\r\n set  Frame_control failed Set %d  read %x", sbuf[1], rbuf[0]);
        return 0;
    }

    printf("\r\n set WUF finish");
    return 1;
}

////////////////////////////////////////////////////////////////////////////////
u8 SIT1145_SleepMode_Set(void)
{
    u8 sbuf[2];
    u8 rbuf[2];

    //清除系统事件状态寄存器
    sbuf[0]=SIT1145_System_event_status;
    sbuf[1]=0xff;
    SIT1145_WriteReg(sbuf,2);

    //清除通讯事件状态寄存器
    sbuf[0]=SIT1145_Transceiver_event_status;
    sbuf[1]=0xff;
    SIT1145_WriteReg(sbuf,2);
    
    //清除唤醒事件状态寄存器
    sbuf[0]=SIT1145_Wake_event;
    sbuf[1]=0xff;
    SIT1145_WriteReg(sbuf,2);

    ////本地唤醒配制
    sbuf[0] = SIT1145_WAKE_enable;
    sbuf[1] = WEAK_PIN_EVEN; 
    SIT1145_WriteReg(sbuf,2);
    SIT1145_ReadReg(rbuf,SIT1145_WAKE_enable,1);
    if((rbuf[0]&WAKE_PIN_EN_WEN) != sbuf[1] )
    {
        printf("\r\n set WAKE_EN failed   %x   ",rbuf[0]);
        return 0;
    }   
    
   //远程唤醒  配制
    sbuf[0] = SIT1145_Transceiver_event_enable;
    sbuf[1] = CAN_BUS_SILENCE_CONF |
                   CAN_FAILURE_CONF |
                   WEAK_PIN_EVEN; 
    SIT1145_WriteReg(sbuf,2);
    SIT1145_ReadReg(rbuf,SIT1145_Transceiver_event_enable,1);
    if((rbuf[0]&TRAN_EVE_WEN) != sbuf[1])
    {
        printf("\r\n set Transceiver_event_enable failed %x", rbuf[0]);
        return 0;
    }   

    //CAN CTRL set
    SIT1145_CANCtrl_Set(CAN_FD_TOLERANCE_EN,CAN_NETW_CONF_SUCCESSFUL,SEL_WAKEUP_EN,CAN_EN_LPC_ACTIVE_MODE);
    
    //讲入睡眠模式
    sbuf[0] = SIT1145_Mode_control;
    sbuf[1] = MC_SLEEP_MODE;
    SIT1145_WriteReg(sbuf,2);
    SIT1145_ReadReg(rbuf,SIT1145_Mode_control,1);
    if((rbuf[0]&MC_MODE_WEN) == MC_SLEEP_MODE)
    {
        printf("\r\n set MC_SLEEP_MODE finished");
    }
    else
    {
        printf("\r\n set MC_SLEEP_MODE failed %d",rbuf[0]);
        return 0;
    }   
    return 1;
}

////////////////////////////////////////////////////////////////////////////////
u8 SIT1145_StandbyMode_Set(void) 
{
    u8 sbuf[2];
    u8 rbuf[2];

    //讲入待机模式
    sbuf[0] = SIT1145_Mode_control;
    sbuf[1] = MC_STANDBY_MODE;
    SIT1145_WriteReg(sbuf,2);
    SIT1145_ReadReg(rbuf,SIT1145_Mode_control,1);
    if((rbuf[0]&MC_MODE_WEN) == MC_STANDBY_MODE)
    {
        printf("\r\n in STANDBYMode finished"); 
        return 1;
    }
    else
    {
        printf("\r\n in STANDBYMode failed %d",rbuf[0]);
        return 0;
    }
}

///////////////////////////////////////////////////////////////////////////////
u8 SIT1145_NormalMode_Set(void)
{
    u8 sbuf[10];
    u8 rbuf[10];

    SIT1145_ReadReg(rbuf,SIT1145_Transceiver_status,1);
    if((rbuf[0] & (1<<TRAN_STA_VCS_POS)) == 1) {
          printf("\\r\n VCC低于欠压检测阈值");
          return 0;
    }

    SIT1145_ReadReg(rbuf,SIT1145_Main_status,1);
    if((rbuf[0] & (1<<MAIN_STA_OTWS_POS)) == 1) {
          printf("\r\n 芯片温度高于警告阈值");
          return 0;
    }

    //CAN CTRL set
    SIT1145_CANCtrl_Set(CAN_FD_TOLERANCE_EN,CAN_NETW_CONF_INVALID,SEL_WAKEUP_DIS,CAN_EN_LPC_ACTIVE_MODE);

     //进入正常模式
    sbuf[0] = SIT1145_Mode_control;
    sbuf[1] = MC_NORMAL_MODE;
    SIT1145_WriteReg(sbuf,2);
    SIT1145_ReadReg(rbuf,SIT1145_Mode_control,1);
    if((rbuf[0]&MC_MODE_WEN) == MC_NORMAL_MODE)
    {
        printf("\r\n in NormalMode finished");
        return 1;
    }
    else
    {
        printf("\r\n in NormalMode failed %d",rbuf[0]);
        return 0;
    }
}

/////////////////////////////////////////////////////////////////////////
void SIT1145_SPICS_Init(void)
{
    GPIO_InitTypeDef  GPIO_InitStructure;
        
    RCC_APB2PeriphClockCmd(RCC_APB2Periph_GPIOB, ENABLE);     //使能PB 端口时钟
   
    GPIO_InitStructure.GPIO_Mode = GPIO_Mode_Out_PP;          //推挽输出
    GPIO_InitStructure.GPIO_Speed = GPIO_Speed_50MHz;         //IO口速度为50MHz

    GPIO_InitStructure.GPIO_Pin = SIT1145_CS_PIN;    
    GPIO_SetBits(SIT1145_SPI_GPIO,SIT1145_CS_PIN);                      
    GPIO_Init(SIT1145_SPI_GPIO, &GPIO_InitStructure);                                                  
}

void SIT1145_Init(void)
{
    SIT1145_SPICS_Init();

    //正常模式
    //SIT1145_NormalMode_Set();

    //待机模式
    //SIT1145_StandbyMode_Set();

    //睡眠模式
    //SIT1145_SleepMode_Set();

    //选择性唤醒模式
    SIT1145_WakeUpFrame_Set();
    SIT1145_SleepMode_Set();

}





