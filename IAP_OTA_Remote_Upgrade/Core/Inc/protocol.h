#ifndef __PROTOCOL_H 
#define __PROTOCOL_H 

#include "stm32f4xx_hal.h"
#include <stdint.h>

/* ==================== 协议格式定义 ==================== */
#define PROTOCOL_HEADER 0xAA55    //包头
#define PROTOCOL_MAX_DATA_LEN 256 //最大数据长度
#define PROTOCOL_MIN_FRAME_LEN 12 //最小帧长度
//帧偏移
#define OFFSET_HEADER 0
#define OFFSET_CMD 2
#define OFFSET_LENGTH 4
#define OFFSET_DATA 6

/* ==================== 命令定义 ==================== */
typedef enum {
    CMD_NONE            = 0x0000,   // 无效命令
    
    // 系统命令（0x01xx）
    CMD_PING            = 0x0100,   // 心跳/Ping
    CMD_RESET           = 0x0101,   // 复位
    CMD_GET_VERSION     = 0x0102,   // 获取版本
    
    // Flash操作命令（0x02xx）
    CMD_FLASH_ERASE     = 0x0200,   // 擦除Flash
    CMD_FLASH_WRITE     = 0x0201,   // 写Flash
    CMD_FLASH_READ      = 0x0202,   // 读Flash
    CMD_FLASH_VERIFY    = 0x0203,   // 校验Flash
    
    // 升级命令（0x03xx）
    CMD_UPDATE_START    = 0x0300,   // 开始升级
    CMD_UPDATE_DATA     = 0x0301,   // 升级数据
    CMD_UPDATE_END      = 0x0302,   // 结束升级
    CMD_UPDATE_ABORT    = 0x0303,   // 中止升级
    
    // Boot命令（0x04xx）
    CMD_BOOT_TO_APP     = 0x0400,   // 跳转到App
    CMD_BOOT_TO_BOOTLOADER = 0x0401,// 进入Bootloader
    CMD_BOOT_SELECT_AREA = 0x0402,  // 选择启动区域
    
    // 参数命令（0x05xx）
    CMD_PARAM_READ      = 0x0500,   // 读取参数
    CMD_PARAM_WRITE     = 0x0501,   // 写入参数
    
    // 响应命令（0xFFxx）
    CMD_ACK             = 0xFF00,   // 应答（成功）
    CMD_NACK            = 0xFF01,   // 应答（失败）
    
} ProtocolCmd_t;

/* ==================== 数据结构 ==================== */
typedef struct{
	uint16_t header;
	uint16_t cmd;
	uint16_t length;
	uint8_t data[PROTOCOL_MAX_DATA_LEN];
	uint32_t reserved;
	uint16_t crc16;
}ProtocolFrame_t;

typedef enum{
	PROTO_IDLE = 0,
	PROTO_RECEVING,
	PROTO_COMPLETE,
	PROTO_ERROR,
}ProtocolStatus_t;

typedef struct{
	uint8_t buffer[PROTOCOL_MIN_FRAME_LEN + PROTOCOL_MAX_DATA_LEN];
	uint16_t index;
	uint16_t expected_len;
	ProtocolStatus_t status;
}ProtocolRxBuffer_t;

/* ==================== 声明函数 ==================== */
// 协议封包
uint16_t Protocol_Pack(ProtocolFrame_t *frame, uint8_t *buffer, uint16_t buffer_size);

// 协议解包
uint8_t Protocol_Unpack(uint8_t *buffer, uint16_t len, ProtocolFrame_t *frame);

// 接收字节处理（用于中断接收）
uint8_t Protocol_ReceiveByte(ProtocolRxBuffer_t *rx_buf, uint8_t byte);

// 创建响应帧
void Protocol_CreateResponse(ProtocolFrame_t *frame, uint16_t cmd, uint8_t *data, uint16_t len);

// 打印帧信息（调试用）
void Protocol_PrintFrame(ProtocolFrame_t *frame);

// 初始化接收缓冲区
void Protocol_InitRxBuffer(ProtocolRxBuffer_t *rx_buf);


#endif
