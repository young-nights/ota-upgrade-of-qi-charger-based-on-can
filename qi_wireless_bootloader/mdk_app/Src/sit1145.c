/**
  **************************************************************************
  * @file     sit1145.c
  * @brief    SIT1145 CAN FD transceiver driver for AT32F426
  *
  * @note     Hardware wiring:
  *           PA4  - SPI1_CS  (GPIO software-controlled, active low)
  *           PA5  - SPI1_SCK (AF MUX0)
  *           PA6  - SPI1_MISO (AF MUX0)
  *           PA7  - SPI1_MOSI (AF MUX0)
  *           PA11 - CAN_RX  (handled by can_driver)
  *           PA12 - CAN_TX  (handled by can_driver)
  *           PB3  - Floating pin (set to output low)
  *
  *           SIT1145 SPI protocol:
  *           Write: CS low -> send (addr << 1) -> send data -> CS high
  *           Read:  CS low -> send (addr << 1) | 0x01 -> read data -> CS high
  **************************************************************************
  */

/* includes ------------------------------------------------------------------*/
#include "sit1145.h"

/* private define ------------------------------------------------------------*/

/**
 * @brief  SPI1 clock configuration
 * @note   APB2 clock assumed 180 MHz (system clock)
 *         Using DIV_128 gives SPI clock ~1.4 MHz, safe for SIT1145
 */
#define SIT1145_SPI_DIV             SPI_MCLK_DIV_128

/* approximate delay for CS setup/hold (~1us at 180MHz) */
#define SIT1145_CS_DELAY_CYCLES     180U

/* private functions ---------------------------------------------------------*/

/**
 * @brief  brief software delay (~1 microsecond)
 * @note   simple busy-wait, calibrated for ~180 MHz system clock
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
 * @brief  busy-wait milliseconds (init path only)
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

/**
 * @brief  wait until transceiver CTS=1 (Normal Mode, ready to drive the bus)
 * @retval 1 if CTS set, 0 on timeout
 */
static uint8_t sit1145_wait_cts(uint32_t timeout_ms)
{
  uint32_t elapsed;
  uint8_t sta;

  for (elapsed = 0U; elapsed < timeout_ms; elapsed++)
  {
    sta = sit1145_read_reg(SIT1145_REG_TRANSCEIVER_STATUS);
    if ((sta & SIT1145_TRAN_STA_CTS) != 0U)
    {
      return 1U;
    }
    sit1145_delay_ms(1U);
  }
  return 0U;
}

/**
 * @brief  assert CS (pull low)
 */
static void sit1145_cs_low(void)
{
  gpio_bits_reset(SIT1145_CS_GPIO_PORT, SIT1145_CS_GPIO_PIN);
  sit1145_delay_us();
}

/**
 * @brief  deassert CS (pull high)
 */
static void sit1145_cs_high(void)
{
  sit1145_delay_us();
  gpio_bits_set(SIT1145_CS_GPIO_PORT, SIT1145_CS_GPIO_PIN);
}

/**
 * @brief  SPI1 send and receive one byte (blocking)
 * @param  tx_data: byte to transmit
 * @retval received byte
 */
static uint8_t sit1145_spi_xfer(uint8_t tx_data)
{
  uint32_t guard = 100000U;

  while ((spi_i2s_flag_get(SPI1, SPI_I2S_TDBE_FLAG) == RESET) && (guard != 0U))
  {
    guard--;
  }
  if (guard == 0U)
  {
    return 0xFFU;
  }

  spi_i2s_data_transmit(SPI1, (uint16_t)tx_data);

  guard = 100000U;
  while ((spi_i2s_flag_get(SPI1, SPI_I2S_RDBF_FLAG) == RESET) && (guard != 0U))
  {
    guard--;
  }
  if (guard == 0U)
  {
    return 0xFFU;
  }

  return (uint8_t)spi_i2s_data_receive(SPI1);
}

/* exported functions --------------------------------------------------------*/

/**
 * @brief  write SIT1145 register via SPI
 * @param  addr: register address
 * @param  data: value to write
 */
void sit1145_write_reg(uint8_t addr, uint8_t data)
{
  sit1145_cs_low();
  sit1145_spi_xfer((uint8_t)(addr << 1) | SIT1145_WRITE);
  sit1145_spi_xfer(data);
  sit1145_cs_high();
}

/**
 * @brief  read SIT1145 register via SPI
 * @param  addr: register address
 * @retval register value
 */
uint8_t sit1145_read_reg(uint8_t addr)
{
  uint8_t val;

  sit1145_cs_low();
  sit1145_spi_xfer((uint8_t)(addr << 1) | SIT1145_READ);
  val = sit1145_spi_xfer(0xFF);
  sit1145_cs_high();

  return val;
}

/**
 * @brief  switch SIT1145 to Normal Mode and verify
 * @note   Bootloader 只切 Normal，不进入 Standby/Sleep。
 */
uint8_t sit1145_normal_mode_set(void)
{
  uint8_t mode_val;
  uint8_t i;

  for (i = 0U; i < 2U; i++)
  {
    sit1145_write_reg(SIT1145_REG_MODE_CONTROL, SIT1145_MC_NORMAL_MODE);
    sit1145_delay_us();
    sit1145_delay_us();
    mode_val = sit1145_read_reg(SIT1145_REG_MODE_CONTROL);
    if ((mode_val & SIT1145_MC_MODE_MASK) == SIT1145_MC_NORMAL_MODE)
    {
      return 1U;
    }
    sit1145_delay_ms(1U);
  }
  return 0U;
}

/**
 * @brief  get current SIT1145 operating mode
 * @retval Mode control register value & 0x07:
 *         SIT1145_MC_SLEEP_MODE   (0x01) — Sleep
 *         SIT1145_MC_STANDBY_MODE (0x04) — Standby
 *         SIT1145_MC_NORMAL_MODE  (0x07) — Normal
 *         0xFF — SPI read error (possibly in Sleep mode)
 */
uint8_t sit1145_get_mode(void)
{
  uint8_t val = sit1145_read_reg(SIT1145_REG_MODE_CONTROL);
  /* if SPI fails (chip in Sleep), read returns 0xFF from spi_xfer timeout */
  return val & SIT1145_MC_MODE_MASK;
}

/**
 * @brief  initialize SIT1145 CAN transceiver
 * @note   performs the following steps:
 *         1. Enable GPIOA and SPI1 peripheral clocks
 *         2. Configure PA5/PA6/PA7 as SPI1 AF pins (MUX0)
 *         3. Configure PA4 as GPIO output (CS, default high)
 *         4. Configure PB3 as GPIO output low (floating pin)
 *         5. Initialize SPI1 Mode 1 (CPOL=0, CPHA=1) — datasheet falling-edge sample
 *         6. Read identification 0x7E (0x70 AQ / 0x74 AQ-FD)
 *         7. Configure CAN Control register (CMC=01, CFDC=1)
 *         8. Configure Data Rate register (250kbps)
 *         9. Switch to Normal Mode
 *        10. Poll CTS until the transmitter is ready
 * @retval 1 on success, 0 on failure
 */
uint8_t sit1145_init(void)
{
  gpio_init_type gpio_init_struct;
  spi_init_type spi_init_struct;
  uint8_t can_ctrl_val;
  uint8_t chip_id;

  /* ---- Step 1: Enable peripheral clocks ---- */
  crm_periph_clock_enable(CRM_GPIOA_PERIPH_CLOCK, TRUE);
  crm_periph_clock_enable(CRM_GPIOB_PERIPH_CLOCK, TRUE);
  crm_periph_clock_enable(CRM_SPI1_PERIPH_CLOCK, TRUE);

  /* ---- Step 2: Configure SPI1 data pins (PA5=SCK, PA6=MISO, PA7=MOSI) ---- */
  /* SCK and MOSI: no pull */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = GPIO_PINS_5 | GPIO_PINS_7;
  gpio_init_struct.gpio_mode           = GPIO_MODE_MUX;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_NONE;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(GPIOA, &gpio_init_struct);

  /* MISO (PA6): pull-up to avoid floating when SIT1145 is not driving */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = GPIO_PINS_6;
  gpio_init_struct.gpio_mode           = GPIO_MODE_MUX;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_UP;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(GPIOA, &gpio_init_struct);

  /* PA5 -> SPI1_SCK (AF MUX0) */
  gpio_pin_mux_config(GPIOA, GPIO_PINS_SOURCE5, GPIO_MUX_0);
  /* PA6 -> SPI1_MISO (AF MUX0) */
  gpio_pin_mux_config(GPIOA, GPIO_PINS_SOURCE6, GPIO_MUX_0);
  /* PA7 -> SPI1_MOSI (AF MUX0) */
  gpio_pin_mux_config(GPIOA, GPIO_PINS_SOURCE7, GPIO_MUX_0);

  /* ---- Step 3: Configure PA4 as CS (GPIO output, default high) ---- */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = SIT1145_CS_GPIO_PIN;
  gpio_init_struct.gpio_mode           = GPIO_MODE_OUTPUT;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_NONE;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(SIT1145_CS_GPIO_PORT, &gpio_init_struct);
  /* set CS high (deselected) */
  gpio_bits_set(SIT1145_CS_GPIO_PORT, SIT1145_CS_GPIO_PIN);

  /* ---- Step 4: Configure PB3 as output low (floating pin) ---- */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins           = SIT1145_FLOAT_GPIO_PIN;
  gpio_init_struct.gpio_mode           = GPIO_MODE_OUTPUT;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_NONE;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_STRONGER;
  gpio_init(SIT1145_FLOAT_GPIO_PORT, &gpio_init_struct);
  gpio_bits_reset(SIT1145_FLOAT_GPIO_PORT, SIT1145_FLOAT_GPIO_PIN);

  /* ---- Step 5: Initialize SPI1 ---- */
  spi_default_para_init(&spi_init_struct);
  spi_init_struct.transmission_mode     = SPI_TRANSMIT_FULL_DUPLEX;
  spi_init_struct.master_slave_mode     = SPI_MODE_MASTER;
  spi_init_struct.mclk_freq_division    = SIT1145_SPI_DIV;
  spi_init_struct.first_bit_transmission = SPI_FIRST_BIT_MSB;
  spi_init_struct.frame_bit_num         = SPI_FRAME_8BIT;
  spi_init_struct.clock_polarity        = SPI_CLOCK_POLARITY_LOW;
  spi_init_struct.clock_phase           = SPI_CLOCK_PHASE_2EDGE;
  spi_init_struct.cs_mode_selection     = SPI_CS_SOFTWARE_MODE;
  spi_init(SPI1, &spi_init_struct);

  /* enable SPI1 */
  spi_enable(SPI1, TRUE);
  sit1145_delay_ms(1U);

  /* ---- Step 6: SPI smoke check via identification register 0x7E ---- */
  chip_id = sit1145_read_reg(SIT1145_REG_IDENTIFICATION);
  if ((chip_id != SIT1145_ID_AQ) && (chip_id != SIT1145_ID_AQ_FD))
  {
    return 0;
  }

  /* ---- Step 7: Configure CAN Control register ----
   *   CFDC=1 (CAN FD tolerance enabled)
   *   PNCOK=0 (partial networking config invalid)
   *   CPNC=0 (selective wakeup disabled)
   *   CMC=01 (active with VCC undervoltage recovery)
   * Must be done before switching to Normal Mode. */
  can_ctrl_val = SIT1145_CAN_FD_TOLERANCE_EN
               | SIT1145_CAN_NETW_INVALID
               | SIT1145_SEL_WAKEUP_DIS
               | SIT1145_CAN_MODE_NORMAL;
  sit1145_write_reg(SIT1145_REG_CAN_CONTROL, can_ctrl_val);
  if (sit1145_read_reg(SIT1145_REG_CAN_CONTROL) != can_ctrl_val)
  {
    return 0;
  }

  /* ---- Step 8: Configure Data Rate (250kbps, partial-network CDR) ---- */
  sit1145_write_reg(SIT1145_REG_DATA_RATE, SIT1145_DEFAULT_DRATE);
  if (sit1145_read_reg(SIT1145_REG_DATA_RATE) != SIT1145_DEFAULT_DRATE)
  {
    return 0;
  }

  /* ---- Step 9: Switch to Normal Mode ----
   *     Required for CAN ACK during wake-up polling.
   *     Standby mode disables TX, so CAN controller cannot receive
   *     frames (no ACK sent → sender retransmits → frame lost). */
  if (sit1145_normal_mode_set() == 0U)
  {
    sit1145_delay_ms(1U);
    if (sit1145_normal_mode_set() == 0U)
    {
      return 0;
    }
  }

  /* ---- Step 10: wait until CTS indicates the transmitter is active ---- */
  if (sit1145_wait_cts(20U) == 0U)
  {
    return 0;
  }

  return 1;
}
