/**
  **************************************************************************
  * @file     board_gpio.h
  * @brief    Board GPIO: NC pins, 12V/5V, single Hall PA0
  **************************************************************************
  */
#ifndef __BOARD_GPIO_H
#define __BOARD_GPIO_H

#ifdef __cplusplus
extern "C" {
#endif

#include "at32f422_426.h"

void    board_gpio_init(void);
void    board_5v_set(uint8_t on);
void    board_12v_buck_set(uint8_t on);
uint8_t board_hall_open(void);
void    board_charge_poll(void);
void    board_charge_set_enable(uint8_t en);

#ifdef __cplusplus
}
#endif

#endif /* __BOARD_GPIO_H */
