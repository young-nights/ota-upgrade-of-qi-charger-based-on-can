/**
  **************************************************************************
  * @file     board_gpio.c
  **************************************************************************
  */
#include "board_gpio.h"

static uint8_t g_charge_enabled = 0U;

void board_gpio_init(void)
{
  gpio_init_type gpio_init_struct;

  crm_periph_clock_enable(CRM_GPIOA_PERIPH_CLOCK, TRUE);
  crm_periph_clock_enable(CRM_GPIOB_PERIPH_CLOCK, TRUE);

  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_mode           = GPIO_MODE_OUTPUT;
  gpio_init_struct.gpio_out_type       = GPIO_OUTPUT_PUSH_PULL;
  gpio_init_struct.gpio_pull           = GPIO_PULL_NONE;
  gpio_init_struct.gpio_drive_strength = GPIO_DRIVE_STRENGTH_MODERATE;

  /* NC: PA1, PA8, PA9, PA10, PA15 output low */
  gpio_init_struct.gpio_pins = GPIO_PINS_1 | GPIO_PINS_8 | GPIO_PINS_9 |
                               GPIO_PINS_10 | GPIO_PINS_15;
  gpio_init(GPIOA, &gpio_init_struct);
  gpio_bits_reset(GPIOA, GPIO_PINS_1 | GPIO_PINS_8 | GPIO_PINS_9 |
                  GPIO_PINS_10 | GPIO_PINS_15);

  /* NC: PB0, PB4, PB5, PB8, PB10 output low (PB3 set by SIT1145) */
  gpio_init_struct.gpio_pins = GPIO_PINS_0 | GPIO_PINS_4 | GPIO_PINS_5 |
                               GPIO_PINS_8 | GPIO_PINS_10;
  gpio_init(GPIOB, &gpio_init_struct);
  gpio_bits_reset(GPIOB, GPIO_PINS_0 | GPIO_PINS_4 | GPIO_PINS_5 |
                  GPIO_PINS_8 | GPIO_PINS_10);

  /* PB1 12V Buck: low = on */
  gpio_init_struct.gpio_pins = GPIO_PINS_1;
  gpio_init(GPIOB, &gpio_init_struct);
  gpio_bits_reset(GPIOB, GPIO_PINS_1);

  /* PB2 5V charger: high = on; default off (low) */
  gpio_init_struct.gpio_pins = GPIO_PINS_2;
  gpio_init(GPIOB, &gpio_init_struct);
  gpio_bits_reset(GPIOB, GPIO_PINS_2);

  /* PA0 Hall H_OUT: input pull-up; no field = high */
  gpio_default_para_init(&gpio_init_struct);
  gpio_init_struct.gpio_pins = GPIO_PINS_0;
  gpio_init_struct.gpio_mode = GPIO_MODE_INPUT;
  gpio_init_struct.gpio_pull = GPIO_PULL_UP;
  gpio_init(GPIOA, &gpio_init_struct);
}

void board_5v_set(uint8_t on)
{
  if (on != 0U)
  {
    gpio_bits_set(GPIOB, GPIO_PINS_2);
  }
  else
  {
    gpio_bits_reset(GPIOB, GPIO_PINS_2);
  }
}

void board_12v_buck_set(uint8_t on)
{
  /* low = enable */
  if (on != 0U)
  {
    gpio_bits_reset(GPIOB, GPIO_PINS_1);
  }
  else
  {
    gpio_bits_set(GPIOB, GPIO_PINS_1);
  }
}

uint8_t board_hall_open(void)
{
  /* PA0 low = magnetic field = DID 0x2118 open */
  return (gpio_input_data_bit_read(GPIOA, GPIO_PINS_0) == RESET) ? 1U : 0U;
}

void board_charge_poll(void)
{
  /* PB2 high only when charger enabled AND hall detects phone (PA0 low) */
  if (g_charge_enabled != 0U && board_hall_open() == 0U)
  {
    board_5v_set(1U);
  }
  else
  {
    board_5v_set(0U);
  }
}

void board_charge_set_enable(uint8_t en)
{
  g_charge_enabled = en;
  if (en == 0U)
  {
    board_5v_set(0U);
  }
}
