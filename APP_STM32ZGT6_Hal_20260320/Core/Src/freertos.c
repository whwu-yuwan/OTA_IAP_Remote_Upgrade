/* USER CODE BEGIN Header */
/**
  ******************************************************************************
  * File Name          : freertos.c
  * Description        : Code for freertos applications
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

/* Includes ------------------------------------------------------------------*/
#include "FreeRTOS.h"
#include "task.h"
#include "main.h"
#include "cmsis_os.h"

/* Private includes ----------------------------------------------------------*/
/* USER CODE BEGIN Includes */
#include <stdio.h>

/* USER CODE END Includes */

/* Private typedef -----------------------------------------------------------*/
/* USER CODE BEGIN PTD */

/* USER CODE END PTD */

/* Private define ------------------------------------------------------------*/
/* USER CODE BEGIN PD */

/* USER CODE END PD */

/* Private macro -------------------------------------------------------------*/
/* USER CODE BEGIN PM */

/* USER CODE END PM */

/* Private variables ---------------------------------------------------------*/
/* USER CODE BEGIN Variables */
osMutexId_t printMutexHandle;
const osMutexAttr_t printMutex_attributes = {
  .name = "printMutex",
};

osThreadId_t heartbeatTaskHandle;
const osThreadAttr_t heartbeatTask_attributes = {
  .name = "heartbeat",
  .stack_size = 512 * 4,
  .priority = (osPriority_t) osPriorityLow,
};

osThreadId_t versionTaskHandle;
const osThreadAttr_t versionTask_attributes = {
  .name = "version",
  .stack_size = 512 * 4,
  .priority = (osPriority_t) osPriorityNormal,
};

/* USER CODE END Variables */
/* Definitions for defaultTask */
osThreadId_t defaultTaskHandle;
const osThreadAttr_t defaultTask_attributes = {
  .name = "defaultTask",
  .stack_size = 256 * 4,
  .priority = (osPriority_t) osPriorityNormal,
};

/* Private function prototypes -----------------------------------------------*/
/* USER CODE BEGIN FunctionPrototypes */
void StartHeartbeatTask(void *argument);
void StartVersionTask(void *argument);

/* USER CODE END FunctionPrototypes */

void StartDefaultTask(void *argument);

void MX_FREERTOS_Init(void); /* (MISRA C 2004 rule 8.1) */

/**
  * @brief  FreeRTOS initialization
  * @param  None
  * @retval None
  */
void MX_FREERTOS_Init(void) {
  /* USER CODE BEGIN Init */

  /* USER CODE END Init */

  /* USER CODE BEGIN RTOS_MUTEX */
  printMutexHandle = osMutexNew(&printMutex_attributes);
  
  /* USER CODE END RTOS_MUTEX */

  /* USER CODE BEGIN RTOS_SEMAPHORES */
  /* add semaphores, ... */
  /* USER CODE END RTOS_SEMAPHORES */

  /* USER CODE BEGIN RTOS_TIMERS */
  /* start timers, add new ones, ... */
  /* USER CODE END RTOS_TIMERS */

  /* USER CODE BEGIN RTOS_QUEUES */
  /* add queues, ... */
  /* USER CODE END RTOS_QUEUES */

  /* Create the thread(s) */
  /* creation of defaultTask */
  defaultTaskHandle = osThreadNew(StartDefaultTask, NULL, &defaultTask_attributes);

  /* USER CODE BEGIN RTOS_THREADS */
  heartbeatTaskHandle = osThreadNew(StartHeartbeatTask, NULL, &heartbeatTask_attributes);
  versionTaskHandle = osThreadNew(StartVersionTask, NULL, &versionTask_attributes);
  
  /* USER CODE END RTOS_THREADS */

  /* USER CODE BEGIN RTOS_EVENTS */
  /* add events, ... */
  /* USER CODE END RTOS_EVENTS */

}

/* USER CODE BEGIN Header_StartDefaultTask */
/**
  * @brief  Function implementing the defaultTask thread.
  * @param  argument: Not used
  * @retval None
  */
/* USER CODE END Header_StartDefaultTask */
void StartDefaultTask(void *argument)
{
  /* USER CODE BEGIN StartDefaultTask */
  /* Infinite loop */
  for(;;)
  {
    osDelay(1);
  }
  /* USER CODE END StartDefaultTask */
}

/* Private application code --------------------------------------------------*/
/* USER CODE BEGIN Application */
void StartHeartbeatTask(void *argument)
{
  uint32_t cnt = 0;
  for(;;)
  {
    osMutexAcquire(printMutexHandle, osWaitForever);
    printf("HB %lu\r\n", (unsigned long)++cnt);
    osMutexRelease(printMutexHandle);
    osDelay(1000U);
  }
}

void StartVersionTask(void *argument)
{
  osDelay(10);
  osMutexAcquire(printMutexHandle, osWaitForever);
  printf("\r\n=== %s ===\r\n", APP_NAME);
  printf("Version: %s\r\n", APP_VERSION_STRING);
  printf("Build: %s %s\r\n", APP_BUILD_DATE, APP_BUILD_TIME);
  printf("HAL: 0x%08lX\r\n", (unsigned long)HAL_GetHalVersion());
  printf("DEVID: 0x%03lX, REVID: 0x%04lX\r\n", (unsigned long)HAL_GetDEVID(), (unsigned long)HAL_GetREVID());
  printf("\r\n");
  osMutexRelease(printMutexHandle);
  osThreadExit();
}

void vApplicationStackOverflowHook(TaskHandle_t xTask, char *pcTaskName)
{
  osMutexAcquire(printMutexHandle, osWaitForever);
  printf("StackOverflow: %s\r\n", pcTaskName);
  osMutexRelease(printMutexHandle);
  taskDISABLE_INTERRUPTS();
  for(;;){}
}

void vApplicationMallocFailedHook(void)
{
  osMutexAcquire(printMutexHandle, osWaitForever);
  printf("MallocFailed\r\n");
  osMutexRelease(printMutexHandle);
  taskDISABLE_INTERRUPTS();
  for(;;){}
}
/* USER CODE END Application */

