#ifndef _FLASH_MANAGE_H
#define _FLASH_MANAGE_H

#include "stm32f4xx_hal.h"
#include <stdint.h>

/*==================flash分区定义===================*/
//Flash基地址
#define FLASH_BASE_ADDR 0x08000000

//boot区 64kb 扇区0-3
#define  BOOT_SECTOR_START FLASH_SECTOR_0
#define  BOOT_SECTOR_END FLASH_SECTOR_3
#define  BOOT_SECTOR_START_ADDR 0x08000000
#define  BOOT_SECTOR_SIZE (1024 * 64)
#define  BOOT_SECTOR_END_ADDR (BOOT_SECTOR_START_ADDR + BOOT_SECTOR_SIZE - 1)

//参数区 64kb 扇区4
#define  PARAM_SECTOR_START FLASH_SECTOR_4
#define  PARAM_SECTOR_END FLASH_SECTOR_4
#define  PARAM_SECTOR_START_ADDR 0x08010000
#define  PARAM_SECTOR_SIZE (1024 * 64)
#define  PARAM_SECTOR_END_ADDR (PARAM_SECTOR_START_ADDR + PARAM_SECTOR_SIZE - 1)

//运行区 256kb 扇区5-6
#define  RUN_SECTOR_START FLASH_SECTOR_5
#define  RUN_SECTOR_END FLASH_SECTOR_6
#define  RUN_SECTOR_START_ADDR 0x08020000
#define  RUN_SECTOR_SIZE (1024 * 256)
#define  RUN_SECTOR_END_ADDR (RUN_SECTOR_START_ADDR + RUN_SECTOR_SIZE - 1)

//APP_A区 256kb 扇区7-8
#define  APP_A_SECTOR_START FLASH_SECTOR_7
#define  APP_A_SECTOR_END FLASH_SECTOR_8
#define  APP_A_SECTOR_START_ADDR 0x08060000
#define  APP_A_SECTOR_SIZE (1024 * 256)
#define  APP_A_SECTOR_END_ADDR (APP_A_SECTOR_START_ADDR + APP_A_SECTOR_SIZE - 1)

//APP_B区 256kb 扇区9-10
#define  APP_B_SECTOR_START FLASH_SECTOR_9
#define  APP_B_SECTOR_END FLASH_SECTOR_10
#define  APP_B_SECTOR_START_ADDR 0x080A0000
#define  APP_B_SECTOR_SIZE (1024 * 256)
#define  APP_B_SECTOR_END_ADDR (APP_B_SECTOR_START_ADDR + APP_B_SECTOR_SIZE - 1)

//预留区 128kb 扇区11
#define  RESERVED_SECTOR_START FLASH_SECTOR_11
#define  RESERVED_SECTOR_END FLASH_SECTOR_11
#define  RESERVED_SECTOR_START_ADDR 0x080E0000
#define  RESERVED_SECTOR_SIZE (1024 * 128)
#define  RESERVED_SECTOR_END_ADDR (RESERVED_SECTOR_START_ADDR + RESERVED_SECTOR_SIZE - 1)

/*==================参数区数据结构定义===================*/
//标志位定义
#define FLAG_VALID 0xAA55AA55
#define FLAG_INVALID 0xFFFFFFFF

//APP区域枚举
typedef enum{
	APP_AREA_NONE = 0,
	APP_AREA_A = 1,
	APP_AREA_B = 2
}AppArea_t;

//APP状态枚举
typedef enum{
	APP_STATUS_INVALID = 0,
	APP_STATUS_VALID = 1,
	APP_STATUS_UPDATING = 2,
	APP_STATUS_ERROR = 3
}AppStatus_t;

//参数区数据结构设计
typedef struct{
	uint32_t valid_flag;
	//boot区信息
	uint32_t boot_version;
	uint32_t boot_run_count;
	//运行区信息
	uint32_t run_app_version;
	uint32_t run_app_size;
	uint32_t run_app_crc32;
	uint32_t run_app_error_count;
	uint32_t run_app_status;
	//APP_A信息
	uint32_t app_a_version;
	uint32_t app_a_size;
	uint32_t app_a_crc32;
	uint32_t app_a_status;
	//APP_B信息
	uint32_t app_b_version;
	uint32_t app_b_size;
	uint32_t app_b_crc32;
	uint32_t app_b_status;
	//区域选择
	AppArea_t boot_select;
	AppArea_t update_target;
	//crc校验
	uint32_t crc32;
	
} FlashParam_t;

/*==================扇区映射表===================*/
typedef struct{
	uint32_t sector_num ;//扇区编号
	uint32_t start_addr ;//扇区地址
	uint32_t size       ;//扇区大小
}FlashSectorInfo_t;

/*==================函数声明===================*/
//获取地址所处的扇区
uint32_t Flash_GetSector(uint32_t addr);

//擦除指定扇区的地址
uint8_t Flash_EraseSector(uint32_t Sector_Start , uint32_t Sector_End);

// 写数据到Flash（addr必须是4字节对齐，len以字节为单位）
uint8_t Flash_Write(uint32_t addr, uint8_t *data, uint32_t len);

// 从Flash读数据（len以字节为单位）
void Flash_Read(uint32_t addr, uint8_t *data, uint32_t len);

// 写入一个32位字
uint8_t Flash_WriteWord(uint32_t addr, uint32_t data);

// 读取一个32位字
uint32_t Flash_ReadWord(uint32_t addr);

// 参数区操作
uint8_t Param_Save(FlashParam_t *param);
uint8_t Param_Load(FlashParam_t *param);
void Param_Init(FlashParam_t *param);
void Param_Print(FlashParam_t *param);

// 打印分区信息
void Flash_PrintPartitionInfo(void);

// Flash自测试函数
uint8_t Flash_SelfTest(void);

//复制Flash数据（addr必须是4字节对齐，len以字节为单位）
uint8_t Flash_Copy(uint32_t dest_addr, uint32_t src_addr, uint32_t len);

#endif
