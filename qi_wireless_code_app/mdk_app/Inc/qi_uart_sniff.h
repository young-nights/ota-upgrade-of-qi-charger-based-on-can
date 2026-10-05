/**
  **************************************************************************
  * @file     qi_uart_sniff.h
  * @brief    Qi UART 线上原始字节抓取缓冲（旁路嗅探，零侵入）
  *
  * 用途：AT32 兼任 UART 嗅探器——把 Qi→AT32 线上全部原始字节
  *       （含帧间杂散、坏帧，不做解析过滤）存入 256B 环形缓冲，
  *       上位机经 CAN-UDS DID 0x2140 分次读出（siphon 语义）。
  *
  * 挂接：qi_uart.c qi_uart_rx_irq_handler()（ISR 级）读出字节后
  *       立即 qi_sniff_feed()——线上真值，连 64B 软环溢出被丢的
  *       字节也能抓到；不改变现有 qi_protocol 解析路径与回调行为。
  *
  * 并发：feed 在 ISR 上下文写（head/count），read 在主循环上下文读
  *       （UDS 0x2140）；取数段用 __disable_irq()/__enable_irq() 包
  *       临界区（模式同 qi_uart.c qi_uart_rx_read()），写侧 ISR 天然
  *       互斥于主循环，无需额外加锁。
  **************************************************************************
  */

#ifndef QI_UART_SNIFF_H
#define QI_UART_SNIFF_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/** @brief  抓取缓冲容量（字节） */
#define QI_SNIFF_BUF_SIZE   256U

/**
 * @brief  喂入一个线上原始字节（ISR 上下文，qi_uart_rx_irq_handler）
 * @note   缓冲满时丢弃新字节并置溢出标志，已有字节不破坏
 * @param  byte: Qi→AT32 方向收到的原始字节
 * @retval none
 */
void qi_sniff_feed(uint8_t byte);

/**
 * @brief  分次读出抓取缓冲（siphon：只清已返回部分）
 * @note   主循环上下文调用；取数段关中断防 ISR 写侧竞争
 * @param  buf:          输出缓冲
 * @param  max:          本次最多取的字节数
 * @param  overflow_flag: 输出——自上次读以来是否发生过溢出丢弃
 *                        （0=无，1=有；读后清零）
 * @retval 实际取出的字节数（0=缓冲空）
 */
uint16_t qi_sniff_read(uint8_t *buf, uint16_t max, uint8_t *overflow_flag);

/**
 * @brief  复位抓取缓冲（清空字节与溢出标志）
 * @retval none
 */
void qi_sniff_reset(void);

#ifdef __cplusplus
}
#endif

#endif /* QI_UART_SNIFF_H */
