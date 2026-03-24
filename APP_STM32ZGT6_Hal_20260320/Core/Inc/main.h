/* USER CODE BEGIN Header */
/**
  ******************************************************************************
  * @file           : main.h
  * @brief          : Header for main.c file.
  *                   This file contains the common defines of the application.
  ******************************************************************************
  * @attention
  *
  * Copyright (c) 2026 STMicroelectronics.
  * All rights reserved.
  *
  * This software is licensed under terms that can be found in the LICENSE file
  * in the root directory of this software component.
  * If no LICENSE file comes with this software, it is provided AS-IS.
  *
  ******************************************************************************
  */
/* USER CODE END Header */

/* Define to prevent recursive inclusion -------------------------------------*/
#ifndef __MAIN_H
#define __MAIN_H

#ifdef __cplusplus
extern "C" {
#endif

/* Includes ------------------------------------------------------------------*/
#include "stm32f4xx_hal.h"

/* Private includes ----------------------------------------------------------*/
/* USER CODE BEGIN Includes */

/* USER CODE END Includes */

/* Exported types ------------------------------------------------------------*/
/* USER CODE BEGIN ET */

/* USER CODE END ET */

/* Exported constants --------------------------------------------------------*/
/* USER CODE BEGIN EC */

/* USER CODE END EC */

/* Exported macro ------------------------------------------------------------*/
/* USER CODE BEGIN EM */

/* USER CODE END EM */

/* Exported functions prototypes ---------------------------------------------*/
void Error_Handler(void);

/* USER CODE BEGIN EFP */

/* USER CODE END EFP */

/* Private defines -----------------------------------------------------------*/

/* USER CODE BEGIN Private defines */
#define APP_NAME                "OTA_IAP_APP"

#define APP_VERSION_MAJOR       1
#define APP_VERSION_MINOR       0
#define APP_VERSION_PATCH       0

#define APP_VERSION_STR_HELPER(x)  #x
#define APP_VERSION_STR(x)         APP_VERSION_STR_HELPER(x)
#define APP_VERSION_STRING         APP_VERSION_STR(APP_VERSION_MAJOR) "." APP_VERSION_STR(APP_VERSION_MINOR) "." APP_VERSION_STR(APP_VERSION_PATCH)

#define APP_BUILD_DATE          __DATE__
#define APP_BUILD_TIME          __TIME__
#define APP_VERSION_FULL        APP_NAME " " APP_VERSION_STRING " (" APP_BUILD_DATE " " APP_BUILD_TIME ")"

#define APP_KEY_Pin             GPIO_PIN_0
#define APP_KEY_GPIO_Port       GPIOA
#define APP_KEY_ACTIVE_STATE    GPIO_PIN_SET
/* USER CODE END Private defines */

#ifdef __cplusplus
}
#endif

#endif /* __MAIN_H */
