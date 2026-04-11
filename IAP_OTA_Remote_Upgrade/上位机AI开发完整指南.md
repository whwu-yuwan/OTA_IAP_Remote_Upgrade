# STM32 IAP/OTA 上位机 AI 开发完整指南（UART / ETH / WiFi）

## 1. 文档目标

本指南用于指导上位机与当前 Bootloader 固件联调，并作为 AI 代码生成输入（Copilot/ChatGPT/Claude 均可）。

覆盖内容：

- 三种通道：UART、ETH、WiFi
- 统一协议帧格式与 CRC 规则
- 命令字、数据结构、升级状态机
- 上位机架构建议（可直接生成代码）
- AI 提示词模板（可复制）
- 联调测试清单与故障排查

适配当前工程实现，不是泛化协议。

---

## 2. 固件侧真实实现摘要

### 2.1 通道角色

- UART：设备作为串口从机，115200 8N1，接收协议帧并回包。
- ETH：设备作为 TCP Server，监听端口 `5000`，上位机作为 TCP Client 连接。
- WiFi：设备通过 AT 模块主动连接上位机 TCP Server（默认 `172.18.241.154:8080`），上位机作为 TCP Server 监听。

### 2.2 通道并发与升级互斥

- 协议层支持三通道收发。
- 升级会话有“通道所有权”：某通道发 `CMD_UPDATE_START` 成功后，`DATA/END` 必须继续走该通道。
- 其他通道抢占会返回 `CMD_BUSY`。

### 2.3 关键工程事实

- `CMD_UPDATE_DATA` 每包必须 `4` 字节对齐。
- 升级区大小上限：`256 KB`（A/B 每区 256KB）。
- 协议最大数据长度：`256` 字节。
- 帧序：小端。
- CRC16 算法：`MODBUS`（初值 `0xFFFF`，查表实现）。
- Boot 时会周期发送 WiFi 心跳 `CMD_PING`（每 1 秒）到 WiFi 通道。

---

## 3. 协议帧规范

### 3.1 帧格式

| 字段 | 偏移 | 长度 | 说明 |
|---|---:|---:|---|
| Header | 0 | 2 | 固定 `0xAA55` |
| Cmd | 2 | 2 | 命令字 |
| Length | 4 | 2 | Data 字节长度 |
| Data | 6 | N | 负载（N=Length） |
| Reserved | 6+N | 4 | 当前固定填 `0x00000000` |
| CRC16 | 10+N | 2 | CRC16（小端） |

### 3.2 约束

- 最小帧长度：`12` 字节
- 最大 `Length`：`256`
- 多字节字段：小端
- CRC16 覆盖范围：从 Header 到 Reserved，不含 CRC16 字段本身

### 3.3 CRC16（MODBUS）

参数：

- 初值：`0xFFFF`
- 多项式：`0xA001`（等价反射写法）
- 输入逐字节更新
- 输出按小端放入帧尾

验证逻辑：

- `calc_crc = CRC16(data[0:len-2])`
- `frame_crc = data[len-2] | (data[len-1] << 8)`
- `calc_crc == frame_crc` 视为通过

---

## 4. 命令字总表

### 4.1 系统命令

| 名称 | 值 | 说明 |
|---|---:|---|
| CMD_PING | `0x0100` | 心跳/回显 |
| CMD_RESET | `0x0101` | 设备复位 |
| CMD_GET_VERSION | `0x0102` | 获取版本 |

### 4.2 升级命令

| 名称 | 值 | 说明 |
|---|---:|---|
| CMD_UPDATE_START | `0x0300` | 开始升级 |
| CMD_UPDATE_DATA | `0x0301` | 升级分片 |
| CMD_UPDATE_END | `0x0302` | 结束升级并 CRC32 校验 |
| CMD_UPDATE_ABORT | `0x0303` | 预留（当前处理器未注册） |

### 4.3 参数命令

| 名称 | 值 | 说明 |
|---|---:|---|
| CMD_PARAM_READ | `0x0500` | 读取参数摘要 |
| CMD_PARAM_WRITE | `0x0501` | 写参数（当前实现为更新计数） |

### 4.4 响应命令

| 名称 | 值 | 说明 |
|---|---:|---|
| CMD_ACK | `0xFF00` | 成功 |
| CMD_NACK | `0xFF01` | 失败 |
| CMD_BUSY | `0xFF02` | 会话忙/通道被占用 |

---

## 5. 命令数据结构

### 5.1 CMD_PING

请求：任意 Data（可空）

响应：`Cmd = CMD_PING`，Data 原样回显

### 5.2 CMD_GET_VERSION

请求：`Length = 0`

响应：`Cmd = CMD_ACK`，Data 结构：

```c
typedef struct {
  uint32_t boot_version;
  uint32_t app_version;
  uint32_t hw_version;
  char build_date[12];
} VersionInfo_t;
```

### 5.3 CMD_PARAM_READ

请求：`Length = 0`

响应：`Cmd = CMD_ACK`，Data 结构：

```c
typedef struct {
  uint32_t boot_version;
  uint32_t boot_run_count;
  uint32_t run_app_version;
  uint8_t  app_a_status;
  uint8_t  app_b_status;
} ParamSummary_t;
```

状态值：

- `0` INVALID
- `1` VALID
- `2` UPDATING
- `3` ERROR

### 5.4 CMD_UPDATE_START

请求 Data：

```c
typedef struct {
  uint32_t target;   // 1=A, 2=B
  uint32_t version;
  uint32_t size;     // 必须 >0 且 4 字节对齐，且 <= 256KB
  uint32_t crc32;    // 整包固件 CRC32
} UpdateStartInfo_t;
```

成功响应：`CMD_ACK`

失败：`CMD_NACK` 或 `CMD_BUSY`

### 5.5 CMD_UPDATE_DATA

请求 Data：固件字节分片

约束：

- `Length > 0`
- `Length % 4 == 0`
- 累计长度不得超过 Start 中声明的 size

成功响应：`CMD_ACK`

失败：`CMD_NACK` 或 `CMD_BUSY`

### 5.6 CMD_UPDATE_END

请求：`Length = 0`

设备行为：

- 校验累计长度是否等于 Start.size
- 对目标区计算 CRC32，与 Start.crc32 比较
- 成功后将目标区状态置 VALID，并切换 boot_select 到目标区

成功响应：`CMD_ACK`

失败：`CMD_NACK` 或 `CMD_BUSY`

---

## 6. 升级状态机（上位机必须实现）

```text
IDLE
  -> CONNECTED
  -> START_SENT
  -> START_ACKED
  -> DATA_LOOP
  -> END_SENT
  -> END_ACKED
  -> DONE

任何阶段收到 NACK -> FAILED
任何阶段收到 BUSY -> RETRY_OR_ABORT
超时/断链 -> RECONNECT_AND_RESUME_OR_ABORT
```

建议策略：

- `BUSY`：指数退避重试（例如 200ms、500ms、1000ms，最多 5 次）
- `NACK`：立即停止并报告具体阶段
- `DATA`：每包等待 ACK，严格串行，不要并发灌包

---

## 7. 三通道接入实现规范

## 7.1 UART 通道规范

参数：

- 115200、8N1、无流控

实现要点：

- 串口接收线程持续读取字节流并喂给“帧重组器”
- 发送后等待对应响应帧（Cmd + 超时）
- 心跳/异步帧与请求响应要分流

推荐超时：

- 普通命令：1000ms
- 升级 Data ACK：2000ms
- Upgrade End：5000ms

## 7.2 ETH 通道规范

角色：

- 设备：TCP Server:5000
- 上位机：TCP Client

实现要点：

- 支持自动重连
- TCP 粘包拆包必须自己做帧解析
- 断线后升级会话可能被设备重置（固件有关闭回调）

## 7.3 WiFi 通道规范

角色：

- 设备内 AT 模块主动 `CIPSTART TCP` 连接上位机
- 上位机必须开 TCP Server（默认 `0.0.0.0:8080`），并确保设备可路由到该 IP

必须注意：

- 设备 WiFi 模块默认会发 `+IPD` 封装，固件已在下行侧解封装
- 你在上位机侧收到的是裸协议帧（无需处理 AT）
- 启动阶段设备会先连 WiFi 再建 socket，建议上位机先起服务再上电设备

---

## 8. 上位机软件架构（建议直接按此生成）

建议分层：

1. `transport` 层
- `UartTransport`
- `TcpClientTransport`（ETH）
- `TcpServerSessionTransport`（WiFi）

2. `codec` 层
- `ProtocolEncoder`
- `ProtocolDecoder`（流式，支持半包粘包）
- `Crc16Modbus`

3. `client` 层
- `BootloaderClient`（Ping/GetVersion/Param/Reset）
- `UpgradeClient`（Start/Data/End）

4. `session` 层
- 请求-响应匹配
- 超时、重试、取消
- 日志与事件回调

5. `ui` 层
- 连接管理
- 固件选择
- 升级进度
- 错误展示

6. `ai_adapter` 层（可选）
- 统一 Prompt 输入输出
- 将“自然语言任务”映射成 SDK 调用

---

## 9. 上位机 AI 代码生成提示词模板

以下模板可直接喂给 AI：

### 9.1 生成协议编码器（Python）

```text
请基于以下协议实现 Python 编解码模块：
- Header=0xAA55，小端
- Frame: header(2)+cmd(2)+len(2)+data(n)+reserved(4)+crc16(2)
- max data len=256
- CRC16 使用 MODBUS，初值0xFFFF
要求：
1) 提供 encode_frame(cmd:int,data:bytes)->bytes
2) 提供流式 Decoder，支持粘包拆包
3) 对 CRC/长度/头错误给出明确异常
4) 提供 pytest 用例（至少10个）
```

### 9.2 生成升级客户端（C#）

```text
请实现 C# BootloaderUpgradeClient，支持 UART/ETH/WiFi 三通道统一接口。
协议见上文。要求：
1) Start/Data/End 串行执行
2) Data 每包长度可配置，默认 256，且 4 字节对齐
3) 每包等待 ACK，超时重试 3 次
4) BUSY 做指数退避，NACK 立即失败
5) 输出进度、速率、剩余时间
6) 可取消（CancellationToken）
7) 给出完整接口定义和示例调用
```

### 9.3 生成前端桌面应用（Electron）

```text
请生成 Electron + React 上位机界面：
- 页面：连接管理、设备信息、升级、日志
- 支持 ETH(client) 和 WiFi(server) 两种 TCP 模式
- 升级流程按 Start->Data->End
- 展示实时进度、错误码、重试次数
- 协议编解码在主进程，UI 只接收事件
- 提供可运行项目结构和关键代码
```

---

## 10. 参考实现伪代码

### 10.1 流式解码器

```pseudo
buffer += incoming_bytes
loop:
  if buffer.length < 12: break
  find header 0x55 0xAA (little endian bytes)
  if not found: keep last 1 byte and break
  if header not at 0: drop before header
  len = read_u16_le(buffer[4:6])
  if len > 256: drop 1 byte and continue
  frame_len = 12 + len
  if buffer.length < frame_len: break
  frame = buffer[0:frame_len]
  if crc16(frame[0:frame_len-2]) != frame_crc: drop 1 byte and continue
  emit frame
  buffer = buffer[frame_len:]
```

### 10.2 升级执行

```pseudo
start(info)
expect ACK
for chunk in firmware_chunks(align=4):
  send DATA(chunk)
  expect ACK
send END()
expect ACK
(optional) send RESET()
```

---

## 11. 关键参数建议

- Data 分片：`256` 字节（若链路差可降到 128）
- 命令超时：`1s`
- Data ACK 超时：`2s`
- End 超时：`5s`
- ETH/WiFi 断线重连：`1~2s` 间隔，最多 `10` 次
- BUSY 重试：最多 `5` 次

---

## 12. 测试清单（必须全过）

### 12.1 协议层

- 正常帧编码/解码
- 半包、粘包、错包头、错 CRC
- `Length=0`、`Length=256` 边界

### 12.2 命令层

- Ping 回显
- GetVersion 结构解析
- ParamRead 结构解析
- 未知命令得到 NACK

### 12.3 升级层

- 正常升级（A 区、B 区）
- Data 非 4 字节对齐 -> NACK
- End CRC32 不匹配 -> NACK
- 中途断线重连策略
- 通道抢占 -> BUSY

### 12.4 通道层

- UART 拔插恢复
- ETH 断网恢复
- WiFi 服务端先起后起、先起设备后起服务端两种场景

---

## 13. 已知约束与注意事项

- `CMD_UPDATE_ABORT` 已定义但当前处理器未注册，不能依赖。
- WiFi 通道底层是 AT 透传，设备端已解 `+IPD`，上位机不要再做 AT 解析。
- 当前固件存在较多日志输出，调试期建议上位机日志时间戳精确到毫秒，便于对齐。
- 设备上电后建议先等待链路就绪再发业务帧（尤其 WiFi）。

---

## 14. 给 AI 的输入最小集合（建议每次都附上）

请把以下内容作为 AI 上下文固定输入：

1. 本文档第 3~8 章
2. 目标语言和框架（例如 Python + PySide6）
3. 你需要的产物（SDK / GUI / CLI / 自动化测试）
4. 质量要求（异常处理、重试、日志、单元测试覆盖率）
5. 验收标准（连接成功率、升级耗时、失败可恢复）

---

## 15. 推荐交付物清单（上位机项目）

- `protocol_codec.*`
- `crc16_modbus.*`
- `transport_uart.*`
- `transport_tcp_client.*`
- `transport_tcp_server.*`（WiFi）
- `bootloader_client.*`
- `upgrade_client.*`
- `app_ui.*`
- `tests_protocol.*`
- `tests_upgrade.*`
- `README.md`（启动、配置、联调步骤）

---

## 16. 一句话执行建议

先用 ETH 跑通协议与升级，再复用同一套协议层到 UART/WiFi；不要为三种通道各写一套命令逻辑。
