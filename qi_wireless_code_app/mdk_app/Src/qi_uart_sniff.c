/**
  **************************************************************************
  * @file     qi_uart_sniff.c
  * @brief    Qi UART 线上原始字节抓取缓冲（旁路嗅探，零侵入）
  *
  * 256B 环形缓冲，捕获 Qi→AT32 全部线上原始字节（含帧间杂散、坏帧，
  * 不做解析过滤）。经 DID 0x2140 分次读出。见 qi_uart_sniff.h 说明。
  **************************************************************************
  */

/* includes ------------------------------------------------------------------*/
#include "qi_uart_sniff.h"
#include <string.h>

/* private variables ---------------------------------------------------------*/

/** @brief  抓取环形缓冲（feed=主循环 qi_uart_poll；read=主循环 UDS 0x2140） */
static uint8_t  sniff_buf[QI_SNIFF_BUF_SIZE];
static uint16_t sniff_head = 0U;   /*!< 写索引 */
static uint16_t sniff_tail  = 0U;   /*!< 读索引 */
static uint16_t sniff_count = 0U;   /*!< 现存字节数 */
static volatile uint8_t sniff_overflow = 0U;  /*!< 溢出丢弃标志（自上次读以来） */

/* exported functions --------------------------------------------------------*/

/**
 * @brief  喂入一个线上原始字节
 * @note   缓冲满：丢弃新字节并置溢出标志，已有字节不破坏
 */
void qi_sniff_feed(uint8_t byte)
{
  if (sniff_count >= QI_SNIFF_BUF_SIZE)
  {
    sniff_overflow = 1U;
    return;
  }
  sniff_buf[sniff_head] = byte;
  sniff_head = (uint16_t)((sniff_head + 1U) % QI_SNIFF_BUF_SIZE);
  sniff_count++;
}

/**
 * @brief  分次读出（siphon：只清已返回部分，剩余字节留待下次）
 * @note   overflow_flag 反映自上次读以来的溢出情况，读后清零
 */
uint16_t qi_sniff_read(uint8_t *buf, uint16_t max, uint8_t *overflow_flag)
{
  uint16_t n = 0U;

  if (overflow_flag != (uint8_t *)0)
  {
    *overflow_flag   = sniff_overflow;
    sniff_overflow   = 0U;
  }

  if (buf == (uint8_t *)0)
  {
    return 0U;
  }

  while ((n < max) && (sniff_count > 0U))
  {
    buf[n] = sniff_buf[sniff_tail];
    sniff_tail = (uint16_t)((sniff_tail + 1U) % QI_SNIFF_BUF_SIZE);
    sniff_count--;
    n++;
  }
  return n;
}

/**
 * @brief  复位抓取缓冲（清空字节与溢出标志）
 */
void qi_sniff_reset(void)
{
  sniff_head = 0U;
  sniff_tail = 0U;
  sniff_count = 0U;
  sniff_overflow = 0U;
  memset((void *)sniff_buf, 0, sizeof(sniff_buf));
}
