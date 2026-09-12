/**
  **************************************************************************
  * @file     can_protocol.h
  * @brief    CAN UDS protocol handler for APP firmware
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

/* define to prevent recursive inclusion -------------------------------------*/
#ifndef __CAN_PROTOCOL_H
#define __CAN_PROTOCOL_H

#ifdef __cplusplus
extern "C" {
#endif

/* includes ------------------------------------------------------------------*/
#include "at32f422_426.h"

/* ========================================================================== */
/*  CAN identifiers                                                          */
/* ========================================================================== */

/** @brief  CAN IDs for UDS communication */
#define CAN_PROTO_UDS_REQUEST        0x18DA0D03U  /*!< UDS request  (tester -> ECU) */
#define CAN_PROTO_UDS_RESPONSE       0x18DA030DU  /*!< UDS response (ECU -> tester) */

/** @brief  Lifecycle broadcast CAN ID (J1939 PDU2, GE=0x26, SA=0x0D) */
#define CAN_ID_LIFECYCLE_BROADCAST   0x18FF260DU

/* ========================================================================== */
/*  UDS service identifiers                                                  */
/* ========================================================================== */

#define UDS_SID_DIAG_SESSION_CTRL    0x10U        /*!< DiagnosticSessionControl */
#define UDS_SID_ECU_RESET            0x11U        /*!< ECUReset */
#define UDS_SID_READ_DATA_BY_ID      0x22U        /*!< ReadDataByIdentifier */
#define UDS_SID_WRITE_DATA_BY_ID     0x2EU        /*!< WriteDataByIdentifier */
#define UDS_SID_SECURITY_ACCESS      0x27U        /*!< SecurityAccess */
#define UDS_SID_ROUTINE_CONTROL      0x31U        /*!< RoutineControl */
#define UDS_SID_REQUEST_DOWNLOAD     0x34U        /*!< RequestDownload */
#define UDS_SID_TRANSFER_DATA        0x36U        /*!< TransferData (boot safe mode only) */
#define UDS_SID_TRANSFER_EXIT        0x37U        /*!< RequestTransferExit (boot safe mode only) */
#define UDS_SID_TRANSFER_SIGNATURE   0x38U        /*!< TransferSignature (boot safe mode only) */
#define UDS_SID_TESTER_KEEPALIVE     0x3EU        /*!< TesterPresent (keepalive) */

/* ========================================================================== */
/*  UDS response codes                                                       */
/* ========================================================================== */

#define UDS_NEGATIVE_RESPONSE        0x7FU        /*!< negative response indicator */
#define UDS_POSITIVE_RESPONSE_OFFSET 0x40U        /*!< positive response offset */

/* ========================================================================== */
/*  UDS negative response codes (NRC)                                        */
/* ========================================================================== */

#define UDS_NRC_SERVICE_NOT_SUPPORTED       0x11U  /*!< service not supported */
#define UDS_NRC_SUBFUNCTION_NOT_SUPPORTED   0x12U  /*!< sub-function not supported */
#define UDS_NRC_INCORRECT_MESSAGE_LENGTH    0x13U  /*!< incorrect message length or invalid format */
#define UDS_NRC_RESPONSE_TOO_LONG           0x14U  /*!< response too long */
#define UDS_NRC_CONDITIONS_NOT_CORRECT      0x22U  /*!< conditions not correct */
#define UDS_NRC_REQUEST_OUT_OF_RANGE        0x31U  /*!< request out of range */
#define UDS_NRC_SECURITY_ACCESS_DENIED      0x33U  /*!< security access denied */
#define UDS_NRC_INVALID_KEY                 0x35U  /*!< invalid key */
#define UDS_NRC_EXCEEDED_NUMBER_OF_ATTEMPTS 0x36U  /*!< exceeded number of attempts */
#define UDS_NRC_REQUIRED_TIME_DELAY         0x37U  /*!< required time delay not expired */
#define UDS_NRC_REQUEST_SEQUENCE_ERROR      0x24U  /*!< request sequence error */
#define UDS_NRC_UPLOAD_DOWNLOAD_NOT_ACCEPTED 0x70U /*!< upload/download not accepted */
#define UDS_NRC_TRANSFER_DATA_SUSPENDED     0x71U  /*!< transfer data suspended */
#define UDS_NRC_GENERAL_PROGRAMMING_FAILURE  0x72U /*!< general programming failure */
#define UDS_NRC_WRONG_BLOCK_SEQUENCE        0x73U  /*!< wrong block sequence counter */
#define UDS_NRC_RESPONSE_PENDING            0x78U  /*!< request correctly received, response pending */

/* ========================================================================== */
/*  UDS response timing constants (P2 / P2*)                                 */
/* ========================================================================== */

#define UDS_P2_TIMEOUT_MS          50U    /*!< P2: max server response time (ms) */
#define UDS_P2_STAR_TIMEOUT_MS     5000U  /*!< P2*: extended timeout after NRC 0x78 (ms) */

/* ========================================================================== */
/*  DID definitions (General + Device-specific)                               */
/* ========================================================================== */

/** @brief  Standard identifier DIDs (per 通用CAN协议规范 8.) */
#define DID_SW_VERSION              0xF195U   /*!< APP software version, 32-byte ASCII */
#define DID_SERIAL_NUMBER           0xF18CU   /*!< serial number from Device Info */
#define DID_BOOTLOADER_VERSION      0xF180U   /*!< bootloader version */
#define DID_HW_VERSION              0xF193U   /*!< hardware version */

/** @brief  Firmware management DIDs */
#define DID_FW_TYPE                 0x2010U   /*!< firmware type, uint8, read/write */

/** @brief  Device-specific DIDs (defined in peripheral SRS) */
#define DID_OTA_STATE               0x2112U   /*!< OTA state from metadata */
#define DID_ACTIVE_SLOT             0x2113U   /*!< active firmware slot */
#define DID_PENDING_SLOT            0x2114U   /*!< pending firmware slot */
#define DID_LAST_BOOT_REASON        0x2115U   /*!< last boot reason */
#define DID_ROLLBACK_COUNT          0x2116U   /*!< rollback counter */
#define DID_CHARGER_CAPABILITY      0x2100U   /*!< charger capability, 4B [max_power_W,0,0,0] */
#define DID_CHARGER_ENABLE          0x2101U   /*!< charger enable, uint8 0/1, write-only */
#define DID_CHARGE_STATE            0x2102U   /*!< charge state machine, uint8 */
#define DID_DEVICE_PRESENT          0x2103U   /*!< device present, uint8 0/1 */
#define DID_OUTPUT_POWER            0x2104U   /*!< output power, uint16 mW */
#define DID_INPUT_VI                0x2105U   /*!< input voltage/current, 2×uint8 */
#define DID_INPUT_CURRENT           0x2106U   /*!< input current, NRC 0x31 (HW not supported) */
#define DID_COIL_TEMP               0x2107U   /*!< coil temperature, NRC 0x31 (HW not supported) */
#define DID_PCB_TEMP                0x2108U   /*!< PCB temperature, uint8 ℃ */
#define DID_FOD_STATUS              0x2109U   /*!< FOD status, uint8 */
#define DID_ALIGNMENT               0x210AU   /*!< alignment status, NRC 0x31 (HW not supported) */
#define DID_FAULT_CODE              0x210BU   /*!< fault code, uint8 */
#define DID_THERMAL_DERATE          0x210CU   /*!< thermal derating level, uint8 */
#define DID_POWER_LIMIT             0x210DU   /*!< power limit, uint16 mW, R/W+NVM (500/1000/1500) */
#define DID_LAST_FAULT_DETAIL       0x2110U   /*!< last fault detail, 4B */
#define DID_CLAMP_STATE             0x2118U   /*!< PA0 hall: 0=closed, 1=open */
#define DID_SIT1145_LP_STATUS       0x2119U   /*!< SIT1145 LP: flags, wup_cnt, last_standby_sec */
#define DID_ECDSA_PUBKEY            0x2120U   /*!< ECDSA P-256 public key, 65-byte SEC1 */
#define DID_QI_IAP_CONTROL          0x2130U   /*!< Qi IAP 控制（写）：启动/中止升级 */
#define DID_QI_IAP_DATA             0x2131U   /*!< Qi IAP 数据（写）：固件数据包 */
#define DID_QI_IAP_STATUS           0x2132U   /*!< Qi IAP 状态（读）：state/progress/版本/已发/总长 */
#define DID_QI_FW_VERSION           0x2133U   /*!< Qi 芯片固件版本（读）：来自 UART 0x01 上报 */

/* ========================================================================== */
/*  Session management constants                                             */
/* ========================================================================== */

#define SESSION_DEFAULT             0x01U     /*!< default session */
#define SESSION_PROGRAMMING         0x02U     /*!< programming session */
#define SESSION_EXTENDED            0x03U     /*!< extended session */

#define SESSION_TIMEOUT_MS          5000U     /*!< TesterPresent timeout in ms */

/** @brief  Suppress positive response bit (bit 7 of sub-function byte) */
#define UDS_SUBFUNC_SUPPRESS_POS_RESP  0x80U
#define UDS_SUBFUNC_MASK               0x7FU

/* ========================================================================== */
/*  Firmware type values (DID 0x2010)                                        */
/* ========================================================================== */

#define FW_TYPE_APP                 0x01U     /*!< APP firmware */
#define FW_TYPE_RESOURCE            0x02U     /*!< resource package */
#define FW_TYPE_BOOTLOADER          0x03U     /*!< bootloader */

/* ========================================================================== */
/*  Qi charging state machine values (DID 0x2102)                            */
/* ========================================================================== */

#define QI_CHARGE_DISABLED          0x00U
#define QI_CHARGE_STANDBY           0x01U
#define QI_CHARGE_DEVICE_DETECTED   0x02U
#define QI_CHARGE_NEGOTIATING       0x03U
#define QI_CHARGE_CHARGING          0x04U
#define QI_CHARGE_COMPLETE          0x05U
#define QI_CHARGE_SUSPENDED_THERMAL 0x06U
#define QI_CHARGE_SUSPENDED_FOD     0x07U
#define QI_CHARGE_FAULT             0x08U
#define QI_CHARGE_SERVICE_MODE      0x09U
#define QI_CHARGE_LOW_POWER         0x0AU

/* ========================================================================== */
/*  NVM offsets for Qi persistent configuration                               */
/* ========================================================================== */

#define NVM_OFFSET_POWER_LIMIT      0x100U    /*!< DID 0x210D power limit, uint16 mW */

/* ========================================================================== */
/*  RoutineControl routine IDs                                               */
/* ========================================================================== */

#define ROUTINE_ERASE_MEMORY        0xFF00U   /*!< erase memory routine (bootloader only) */

/* ========================================================================== */
/*  Exported functions                                                       */
/* ========================================================================== */

/**
 * @brief  initialize CAN protocol module
 * @note   registers CAN RX callback with the CAN driver.
 *         must be called after can_driver_init().
 * @param  none
 * @retval none
 */
void can_protocol_init(void);

/**
 * @brief  poll session timeout (S3) and ISO-TP N_Cr
 * @note   call from the main loop.
 */
void can_protocol_poll(void);

/**
 * @brief  1 if SIT1145 is in Normal and CAN1 is online (UDS/lifecycle allowed)
 */
uint8_t can_protocol_is_bus_awake(void);

/**
 * @brief  1 if lifecycle CAN TX is allowed (awake and past post-wake quiet)
 * @note   唤醒后先给 UDS 应答让路，quiet 期内禁止 BOOTUP。
 */
uint8_t can_protocol_lifecycle_tx_ready(void);

/**
 * @brief  get current diagnostic session
 * @retval SESSION_DEFAULT, SESSION_PROGRAMMING, or SESSION_EXTENDED
 */
uint8_t can_protocol_get_session(void);

/**
 * @brief  check if security access Level 1 is unlocked
 * @retval 1 = unlocked, 0 = locked
 */
uint8_t can_protocol_is_security_unlocked(void);

#ifdef __cplusplus
}
#endif

#endif /* __CAN_PROTOCOL_H */
