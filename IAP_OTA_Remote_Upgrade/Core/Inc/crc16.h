#ifndef __CRC16_H
#define __CRC16_H

#include <stdint.h>

/* ==================== CRC16配置 ==================== */

// CRC16算法选择
#define CRC16_CCITT_FALSE   0   // CCITT-FALSE（初值0xFFFF，多项式0x1021，不反转）
#define CRC16_MODBUS        1   // MODBUS（初值0xFFFF，多项式0x8005，反转）
#define CRC16_IBM           2   // IBM（初值0x0000，多项式0x8005）

// 选择使用的CRC16算法
#define CRC16_ALGORITHM     CRC16_MODBUS  // 推荐使用MODBUS

/* ==================== 函数声明 ==================== */

/**
 * @brief  计算CRC16
 * @param  data: 数据指针
 * @param  len: 数据长度（字节）
 * @retval CRC16值
 */
uint16_t CRC16_Calculate(uint8_t *data, uint16_t len);

/**
 * @brief  验证CRC16
 * @param  data: 数据指针（包含CRC16）
 * @param  len: 总长度（包含CRC16）
 * @retval 0:校验通过 1:校验失败
 */
uint8_t CRC16_Verify(uint8_t *data, uint16_t len);

#endif /* __CRC16_H */

