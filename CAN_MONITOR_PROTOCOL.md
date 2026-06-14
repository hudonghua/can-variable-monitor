# CAN Variable Monitor Protocol

This is the firmware-side protocol expected by `CanVariableMonitor`.

## CAN IDs

- Request: `0x7F0`
- Response: `0x7F1`

Both IDs can be changed in the upper-computer UI.

## Read Request

PC to controller:

| Byte | Meaning |
| --- | --- |
| 0 | `0xA5` read command |
| 1 | sequence number |
| 2 | address byte 0, low |
| 3 | address byte 1 |
| 4 | address byte 2 |
| 5 | address byte 3, high |
| 6 | read length, `1` or `2` |
| 7 | checksum, sum of byte 0..6 |

## Read Response

Controller to PC:

| Byte | Meaning |
| --- | --- |
| 0 | `0x5A` read response |
| 1 | same sequence number |
| 2 | read length |
| 3 | status, `0` OK, `1` bad address, `2` bad length |
| 4 | value low byte |
| 5 | value high byte |
| 6 | reserved |
| 7 | checksum, sum of byte 0..6 |

## Firmware Example

```c
#define MON_REQ_ID       0x7F0
#define MON_ACK_ID       0x7F1
#define MON_CMD_READ     0xA5
#define MON_ACK_READ     0x5A

static unsigned char Mon_CheckSum(unsigned char *p, unsigned char len)
{
    unsigned char i;
    unsigned char sum = 0;
    for(i = 0; i < len; i++)
        sum += p[i];
    return sum;
}

static unsigned char Mon_IsRamAddr(unsigned long addr, unsigned char len)
{
    unsigned long end = addr + len - 1;

    if((addr >= 0x10000000) && (end <= 0x10003FFF))
        return 1;
    if((addr >= 0x2007C000) && (end <= 0x2007FFFF))
        return 1;
    return 0;
}

void Mon_CanRead(unsigned char *rx)
{
    unsigned char tx[8] = {0};
    unsigned long addr;
    unsigned short value = 0;
    unsigned char len;
    unsigned char status = 0;

    if(rx[0] != MON_CMD_READ)
        return;
    if(rx[7] != Mon_CheckSum(rx, 7))
        return;

    addr  = ((unsigned long)rx[2]);
    addr |= ((unsigned long)rx[3]) << 8;
    addr |= ((unsigned long)rx[4]) << 16;
    addr |= ((unsigned long)rx[5]) << 24;
    len = rx[6];

    if((len != 1) && (len != 2))
    {
        status = 2;
    }
    else if(!Mon_IsRamAddr(addr, len))
    {
        status = 1;
    }
    else if(len == 1)
    {
        value = *((volatile unsigned char *)addr);
    }
    else
    {
        value = *((volatile unsigned short *)addr);
    }

    tx[0] = MON_ACK_READ;
    tx[1] = rx[1];
    tx[2] = len;
    tx[3] = status;
    tx[4] = value & 0xff;
    tx[5] = value >> 8;
    tx[6] = 0;
    tx[7] = Mon_CheckSum(tx, 7);

    CAN_SendXLen(MON_ACK_ID, tx, 8);
}
```

Integration points for the HP6-style project:

```c
RegisterID(MON_REQ_ID, i++, 2000);

rdCan = CAN1_Get_Data(MON_REQ_ID);
if(rdCan < ID_RCV_NUM)
{
    Mon_CanRead(CAN1_RBuf);
}
```

