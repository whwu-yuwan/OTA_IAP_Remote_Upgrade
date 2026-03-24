#include "protocol.h"
#include "crc16.h"
#include <stdio.h>
#include <string.h>

// 协议封包
uint16_t Protocol_Pack(ProtocolFrame_t *frame, uint8_t *buffer, uint16_t buffer_size){
	uint16_t total_size = frame->length + PROTOCOL_MIN_FRAME_LEN;
	uint16_t index = 0;
	//缓冲区内存不足
	if (buffer_size < total_size){
		return 0;
	}
	//包头(小端序)
	buffer[index++] = (uint8_t)(frame->header & 0xFF);
	buffer[index++] = (uint8_t )(frame->header >> 8);
	
	//cmd(小端序)
	buffer[index++] = (uint8_t)(frame->cmd & 0xFF);
	buffer[index++] = (uint8_t )(frame->cmd >> 8);
	
	//length(小端序)
	buffer[index++] = (uint8_t)(frame->length & 0xFF);
	buffer[index++] = (uint8_t )(frame->length >> 8);
	//data
	if (frame->length > 0){
		memcpy(&buffer[index], frame->data, frame->length);
		index += frame->length;
	}   
	//保留区(小端序)
	buffer[index++] = (uint8_t)(frame->reserved & 0xFF);
	buffer[index++] = (uint8_t)((frame->reserved >> 8)& 0xFF);
	buffer[index++] = (uint8_t)((frame->reserved >> 16)& 0xFF);
	buffer[index++] = (uint8_t)(frame->reserved >> 24);
	
	frame->crc16 = CRC16_Calculate(buffer, index);
	//crc16
	buffer[index++] = (uint8_t)(frame->crc16 & 0xFF);
	buffer[index++] = (uint8_t)(frame->crc16 >> 8);
	
	return index;
}

// 协议解包
uint8_t Protocol_Unpack(uint8_t *buffer, uint16_t len, ProtocolFrame_t *frame){
	uint16_t index = 0;
	
	if (PROTOCOL_MIN_FRAME_LEN > len){
		return 1;
	}
	
	frame->header = buffer[index] | buffer[index + 1] << 8; 
	if (frame->header != PROTOCOL_HEADER){
		return 1;
	}
	index += 2;
	
	frame->cmd = buffer[index] | buffer[index + 1] << 8;
	index += 2;
	
	
	frame->length = buffer[index] | buffer[index + 1] << 8;
	index += 2;
	
    if (frame->length > PROTOCOL_MAX_DATA_LEN) {
        return 1;  
    }

    uint16_t expected_len = PROTOCOL_MIN_FRAME_LEN + frame->length;
    if (len < expected_len) {
		printf("ex:%02X , now::%02X\r\n" , expected_len , len);
        return 1;  
    }
	
	if (frame->length > 0){
		memcpy(frame->data, &buffer[index], frame->length);
		index += frame->length;
	}
	
	frame->reserved = buffer[index] | buffer[index + 1] << 8 | buffer[index + 2] << 16 | buffer[index + 3] << 24;
	index += 4;
	
	frame->crc16 = buffer[index] | buffer[index + 1] << 8;
	index += 2;
	if (frame->crc16 != CRC16_Calculate(buffer, len - 2)){
		printf("[CRC16] calculate error\r\n");
		return 1;
	}
	
	return 0;
}

// 接收字节处理（用于中断接收）
uint8_t Protocol_ReceiveByte(ProtocolRxBuffer_t *rx_buf, uint8_t byte){
	switch(rx_buf->status){
		case PROTO_IDLE:
			if (byte == (PROTOCOL_HEADER & 0xFF)){
				rx_buf->buffer[rx_buf->index++] = byte;
				rx_buf->status = PROTO_RECEVING;
			}
			break;
		case PROTO_RECEVING:
			rx_buf->buffer[rx_buf->index++] = byte;
			if (rx_buf->index == 2){
				uint16_t header = rx_buf->buffer[0]| rx_buf->buffer[1] << 8;
				if (header != PROTOCOL_HEADER){
					rx_buf->status = PROTO_ERROR;
					return 2;
				}
			}
			if (rx_buf->index == 6){
				uint16_t data_len = rx_buf->buffer[4] | rx_buf->buffer[5] << 8;
				if (data_len > PROTOCOL_MAX_DATA_LEN){
					rx_buf->status = PROTO_ERROR;
					return 2;
				}
				rx_buf->expected_len = data_len;
			}
			if (rx_buf->index == rx_buf->expected_len + 12){
				rx_buf->status = PROTO_COMPLETE;
				return 1;
			}
		case PROTO_COMPLETE: 
		case PROTO_ERROR:
			break;
	}
	return 0; 
}

//初始化缓冲池
void Protocol_InitRxBuffer(ProtocolRxBuffer_t *rx_buf)
{
    memset(rx_buf, 0, sizeof(ProtocolRxBuffer_t));
    rx_buf->status = PROTO_IDLE;
}

//创建响应帧
void Protocol_CreateResponse(ProtocolFrame_t *frame, uint16_t cmd, uint8_t *data, uint16_t len)
{
    frame->header = PROTOCOL_HEADER;
    frame->cmd = cmd;
    frame->length = len;
    
    if (len > 0 && len <= PROTOCOL_MAX_DATA_LEN) {
        memcpy(frame->data, data, len);
    }
    
    frame->reserved = 0x00000000;
    frame->crc16 = 0;  // 封包时会自动计算
}


void Protocol_PrintFrame(ProtocolFrame_t *frame)
{
    printf("\r\n========== Protocol Frame ==========\r\n");
    printf("Header:   0x%04X\r\n", frame->header);
    printf("Command:  0x%04X\r\n", frame->cmd);
    printf("Length:   %d bytes\r\n", frame->length);
    
    if (frame->length > 0) {
        printf("Data:     ");
        for (int i = 0; i < frame->length && i < 16; i++) {
            printf("%02X ", frame->data[i]);
        }
        if (frame->length > 16) {
            printf("... (%d more)", frame->length - 16);
        }
        printf("\r\n");
    }
    
    printf("Reserved: 0x%08dX\r\n", frame->reserved);
    printf("CRC16:    0x%04X\r\n", frame->crc16);
    printf("====================================\r\n\r\n");
}
