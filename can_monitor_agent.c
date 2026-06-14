/*
 * CAN variable monitor agent for Keil/Cortex-M projects.
 *
 * This file is used by the monitor upper computer.
 * It stays quiet until the upper computer connects, then serves variable
 * values and business trace points over CAN.
 *
 * Integration for HP6-style projects:
 *   1. Add this file to the Keil project source group.
 *   2. Call CanMonitor_Process() from the normal CAN receive/main-loop path.
 *   3. Call CanMonitor_BusinessGate() before the business logic entry.
 *   4. Optionally call CanMonitor_Trace(id) at key business points.
 *
 * The agent automatically senses the first free CAN receive table slot and
 * registers MON_REQ_ID. You do not need to manually calculate the i++ order.
 *
 * Note:
 *   Merely copying this file to Src is not enough for classic Keil projects
 *   unless the .uvproj already compiles every .c file automatically and calls
 *   CanMonitor_Process() from existing code.
 */

#include "CanOpen.h"

#define CAN_MONITOR_AGENT_VERSION 0x20260624UL

#ifndef CAN_MONITOR_ENABLE_CAN2
#define CAN_MONITOR_ENABLE_CAN2 0
#endif

#ifndef MON_REQ_ID
#define MON_REQ_ID       0x7F0
#endif

#ifndef MON_ACK_ID
#define MON_ACK_ID       0x7F1
#endif

#define MON_CMD_READ     0xA5
#define MON_ACK_READ     0x5A
#define MON_CMD_OPEN     0xA0
#define MON_CMD_CLOSE    0xA1
#define MON_CMD_REBOOT   0xA2
#define MON_ACK_REBOOT   0xA3
#define MON_CMD_WRITE1   0xA6
#define MON_CMD_WRITE2   0xA7
#define MON_CMD_FORCE1   0xA8
#define MON_CMD_FORCE2   0xA9
#define MON_CMD_RELEASE  0xAA
#define MON_ACK_WRITE    0x6A
#define MON_CMD_CONTROL  0xAC
#define MON_ACK_CONTROL  0xCA
#define MON_CMD_TRACE    0xB0
#define MON_ACK_TRACE    0xB1
#define MON_ONLINE_WINDOW 1
#define MON_FORCE_SLOTS  8
#define MON_RUN_NORMAL   0
#define MON_RUN_PAUSE    1
#define MON_RUN_STEP     2

extern unsigned char CAN1_RBuf[8];
extern unsigned long gRcvCanID[2][ID_RCV_NUM];
extern unsigned char CAN1_Get_Data(unsigned int vID);
extern void CAN_SendXLen(unsigned long vID, unsigned char* vpData, char vLen);

#if CAN_MONITOR_ENABLE_CAN2
extern unsigned char CAN2_RBuf[8];
extern unsigned long gRcvCan2ID[2][ID_RCV_NUM];
extern unsigned char CAN2_Get_Data(unsigned int vID);
extern void CAN2_SendXLen(unsigned long vID, unsigned char* vpData, char vLen);
#endif

typedef void (*CanMonitor_SendFunc)(unsigned long vID, unsigned char* vpData, char vLen);

typedef struct
{
    unsigned char active;
    unsigned char len;
    unsigned long addr;
    unsigned short value;
} CanMonitor_ForceSlot;

static unsigned char gCanMonitorOnline = 0;
static volatile unsigned short gCanMonitorTraceId = 0;
static unsigned char gCanMonitorLastRxValid = 0;
static unsigned char gCanMonitorLastRx[8];
static CanMonitor_ForceSlot gCanMonitorForceSlots[MON_FORCE_SLOTS];
static unsigned char gCanMonitorRunMode = MON_RUN_NORMAL;
static unsigned char gCanMonitorStepPending = 0;

static unsigned char CanMonitor_CheckSum(unsigned char *p, unsigned char len)
{
    unsigned char i;
    unsigned char sum = 0;

    for(i = 0; i < len; i++)
    {
        sum += p[i];
    }

    return sum;
}

static unsigned char CanMonitor_IsRamAddr(unsigned long addr, unsigned char len)
{
    unsigned long end;

    if(len == 0)
    {
        return 0;
    }

    end = addr + len - 1;

    if((addr >= 0x10000000) && (end <= 0x10003FFF))
    {
        return 1;
    }

    if((addr >= 0x2007C000) && (end <= 0x2007FFFF))
    {
        return 1;
    }

    return 0;
}

static unsigned char CanMonitor_WriteMemory(unsigned long addr, unsigned char len, unsigned short value)
{
    if((len != 1) && (len != 2))
    {
        return 2;
    }

    if(!CanMonitor_IsRamAddr(addr, len))
    {
        return 1;
    }

    if(len == 1)
    {
        *((volatile unsigned char *)addr) = (unsigned char)(value & 0xff);
    }
    else
    {
        *((volatile unsigned short *)addr) = value;
    }

    return 0;
}

static void CanMonitor_ApplyForces(void)
{
    unsigned char i;

    for(i = 0; i < MON_FORCE_SLOTS; i++)
    {
        if(gCanMonitorForceSlots[i].active)
        {
            CanMonitor_WriteMemory(
                gCanMonitorForceSlots[i].addr,
                gCanMonitorForceSlots[i].len,
                gCanMonitorForceSlots[i].value);
        }
    }
}

static unsigned char CanMonitor_SetForce(unsigned long addr, unsigned char len, unsigned short value)
{
    unsigned char i;
    unsigned char freeIndex;
    unsigned char status;

    status = CanMonitor_WriteMemory(addr, len, value);
    if(status != 0)
    {
        return status;
    }

    freeIndex = 0xff;
    for(i = 0; i < MON_FORCE_SLOTS; i++)
    {
        if(gCanMonitorForceSlots[i].active &&
           (gCanMonitorForceSlots[i].addr == addr) &&
           (gCanMonitorForceSlots[i].len == len))
        {
            gCanMonitorForceSlots[i].value = value;
            return 0;
        }

        if((freeIndex == 0xff) && (!gCanMonitorForceSlots[i].active))
        {
            freeIndex = i;
        }
    }

    if(freeIndex == 0xff)
    {
        return 3;
    }

    gCanMonitorForceSlots[freeIndex].active = 1;
    gCanMonitorForceSlots[freeIndex].addr = addr;
    gCanMonitorForceSlots[freeIndex].len = len;
    gCanMonitorForceSlots[freeIndex].value = value;
    return 0;
}

static unsigned char CanMonitor_ReleaseForce(unsigned long addr)
{
    unsigned char i;

    for(i = 0; i < MON_FORCE_SLOTS; i++)
    {
        if(gCanMonitorForceSlots[i].active && (gCanMonitorForceSlots[i].addr == addr))
        {
            gCanMonitorForceSlots[i].active = 0;
        }
    }

    return 0;
}

static void CanMonitor_SendWriteAck(CanMonitor_SendFunc sendFunc, unsigned char seq, unsigned char status, unsigned char len, unsigned char command)
{
    unsigned char tx[8] = {0};

    tx[0] = MON_ACK_WRITE;
    tx[1] = seq;
    tx[2] = status;
    tx[3] = len;
    tx[4] = command;
    tx[5] = 0;
    tx[6] = 0;
    tx[7] = CanMonitor_CheckSum(tx, 7);
    sendFunc(MON_ACK_ID, tx, 8);
}

static unsigned char CanMonitor_IsRuntimeControlFrame(unsigned char *rx)
{
    if(rx[7] != CanMonitor_CheckSum(rx, 7))
    {
        return 0;
    }

    if((rx[2] != 'K') || (rx[3] != 'X') || (rx[4] != 'R') || (rx[5] != 'T'))
    {
        return 0;
    }

    return 1;
}

static void CanMonitor_SendControlAck(CanMonitor_SendFunc sendFunc, unsigned char seq, unsigned char status, unsigned char mode)
{
    unsigned char tx[8] = {0};

    tx[0] = MON_ACK_CONTROL;
    tx[1] = seq;
    tx[2] = status;
    tx[3] = mode;
    tx[4] = 0;
    tx[5] = 0;
    tx[6] = 0;
    tx[7] = CanMonitor_CheckSum(tx, 7);
    sendFunc(MON_ACK_ID, tx, 8);
}

static unsigned char CanMonitor_RegisterTable(unsigned long table[2][ID_RCV_NUM])
{
    unsigned char i;

    for(i = 0; i < ID_RCV_NUM; i++)
    {
        if(table[0][i] == MON_REQ_ID)
        {
            return 1;
        }
    }

    for(i = 0; i < ID_RCV_NUM; i++)
    {
        if(table[0][i] == 0)
        {
            table[0][i] = MON_REQ_ID;
            table[1][i] = 2000;
            return 1;
        }
    }

    return 0;
}

unsigned char CanMonitor_Register(void)
{
    unsigned char ok;

    ok = CanMonitor_RegisterTable(gRcvCanID);

#if CAN_MONITOR_ENABLE_CAN2
    if(CanMonitor_RegisterTable(gRcvCan2ID))
    {
        ok = 1;
    }
#endif

    return ok;
}

static unsigned char CanMonitor_IsSessionFrame(unsigned char *rx)
{
    if(rx[7] != CanMonitor_CheckSum(rx, 7))
    {
        return 0;
    }

    if((rx[2] != 'K') || (rx[3] != 'X') || (rx[4] != 'M') || (rx[5] != 'O') || (rx[6] != 'N'))
    {
        return 0;
    }

    return 1;
}

static void CanMonitor_SystemReset(void)
{
    volatile unsigned long delay;

    for(delay = 0; delay < 800000UL; delay++)
    {
    }

    *((volatile unsigned long *)0xE000ED0C) = 0x05FA0004UL;

    while(1)
    {
    }
}

void CanMonitor_Trace(unsigned short traceId)
{
    if(gCanMonitorOnline != 0)
    {
        gCanMonitorTraceId = traceId;
    }
}

unsigned char CanMonitor_BusinessGate(void)
{
    CanMonitor_ApplyForces();

    if(gCanMonitorRunMode == MON_RUN_NORMAL)
    {
        return 1;
    }

    if(gCanMonitorStepPending != 0)
    {
        gCanMonitorStepPending = 0;
        return 1;
    }

    return 0;
}

void CanMonitor_Handle(unsigned char *rx, CanMonitor_SendFunc sendFunc)
{
    unsigned char tx[8] = {0};
    unsigned long addr;
    unsigned short value = 0;
    unsigned char len;
    unsigned char status = 0;
    unsigned char command;

    if(rx[0] == MON_CMD_OPEN)
    {
        if(CanMonitor_IsSessionFrame(rx))
        {
            gCanMonitorOnline = MON_ONLINE_WINDOW;
            gCanMonitorRunMode = MON_RUN_NORMAL;
            gCanMonitorStepPending = 0;
        }
        return;
    }

    if(rx[0] == MON_CMD_CLOSE)
    {
        if(CanMonitor_IsSessionFrame(rx))
        {
            gCanMonitorOnline = 0;
            gCanMonitorRunMode = MON_RUN_NORMAL;
            gCanMonitorStepPending = 0;
        }
        return;
    }

    if(rx[0] == MON_CMD_REBOOT)
    {
        if(CanMonitor_IsSessionFrame(rx))
        {
            tx[0] = MON_ACK_REBOOT;
            tx[1] = rx[1];
            tx[2] = 0;
            tx[3] = 0;
            tx[4] = 0;
            tx[5] = 0;
            tx[6] = 0;
            tx[7] = CanMonitor_CheckSum(tx, 7);
            sendFunc(MON_ACK_ID, tx, 8);
            CanMonitor_SystemReset();
        }
        return;
    }

    if(rx[0] == MON_CMD_TRACE)
    {
        if(CanMonitor_IsSessionFrame(rx))
        {
            tx[0] = MON_ACK_TRACE;
            tx[1] = rx[1];
            tx[2] = gCanMonitorTraceId & 0xff;
            tx[3] = gCanMonitorTraceId >> 8;
            tx[4] = 0;
            tx[5] = 0;
            tx[6] = 0;
            tx[7] = CanMonitor_CheckSum(tx, 7);
            sendFunc(MON_ACK_ID, tx, 8);
        }
        return;
    }

    if((rx[0] == MON_CMD_WRITE1) || (rx[0] == MON_CMD_WRITE2) ||
       (rx[0] == MON_CMD_FORCE1) || (rx[0] == MON_CMD_FORCE2) ||
       (rx[0] == MON_CMD_RELEASE))
    {
        command = rx[0];
        addr  = ((unsigned long)rx[2]);
        addr |= ((unsigned long)rx[3]) << 8;
        addr |= ((unsigned long)rx[4]) << 16;
        addr |= ((unsigned long)rx[5]) << 24;
        value = (unsigned short)(((unsigned short)rx[6]) | (((unsigned short)rx[7]) << 8));

        if((command == MON_CMD_WRITE1) || (command == MON_CMD_FORCE1))
        {
            len = 1;
        }
        else
        {
            len = 2;
        }

        if(gCanMonitorOnline == 0)
        {
            status = 4;
        }
        else if(command == MON_CMD_RELEASE)
        {
            status = CanMonitor_ReleaseForce(addr);
            len = 0;
        }
        else if((command == MON_CMD_FORCE1) || (command == MON_CMD_FORCE2))
        {
            status = CanMonitor_SetForce(addr, len, value);
        }
        else
        {
            status = CanMonitor_WriteMemory(addr, len, value);
        }

        CanMonitor_SendWriteAck(sendFunc, rx[1], status, len, command);
        return;
    }

    if(rx[0] == MON_CMD_CONTROL)
    {
        if(CanMonitor_IsRuntimeControlFrame(rx))
        {
            if(rx[6] == MON_RUN_NORMAL)
            {
                gCanMonitorRunMode = MON_RUN_NORMAL;
                gCanMonitorStepPending = 0;
                status = 0;
            }
            else if(rx[6] == MON_RUN_PAUSE)
            {
                gCanMonitorRunMode = MON_RUN_PAUSE;
                gCanMonitorStepPending = 0;
                status = 0;
            }
            else if(rx[6] == MON_RUN_STEP)
            {
                gCanMonitorRunMode = MON_RUN_PAUSE;
                gCanMonitorStepPending = 1;
                status = 0;
            }
            else
            {
                status = 2;
            }

            CanMonitor_SendControlAck(sendFunc, rx[1], status, rx[6]);
        }
        return;
    }

    if(rx[0] != MON_CMD_READ)
    {
        return;
    }

    if(rx[7] != CanMonitor_CheckSum(rx, 7))
    {
        return;
    }

    addr  = ((unsigned long)rx[2]);
    addr |= ((unsigned long)rx[3]) << 8;
    addr |= ((unsigned long)rx[4]) << 16;
    addr |= ((unsigned long)rx[5]) << 24;
    len = rx[6];

    if((len != 1) && (len != 2))
    {
        status = 2;
    }
    else if(!CanMonitor_IsRamAddr(addr, len))
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
    tx[7] = CanMonitor_CheckSum(tx, 7);

    sendFunc(MON_ACK_ID, tx, 8);
}

static unsigned char CanMonitor_IsRepeatedRx(unsigned char *rx)
{
    unsigned char i;

    if(!gCanMonitorLastRxValid)
    {
        gCanMonitorLastRxValid = 1;
        for(i=0; i<8; i++)
        {
            gCanMonitorLastRx[i] = rx[i];
        }
        return 0;
    }

    for(i=0; i<8; i++)
    {
        if(gCanMonitorLastRx[i] != rx[i])
        {
            break;
        }
    }

    if(i >= 8)
    {
        return 1;
    }

    for(i=0; i<8; i++)
    {
        gCanMonitorLastRx[i] = rx[i];
    }
    return 0;
}

void CanMonitor_Process(void)
{
    unsigned char rdCan;

    CanMonitor_ApplyForces();

    if(!CanMonitor_Register())
    {
        return;
    }

    rdCan = CAN1_Get_Data(MON_REQ_ID);
    if(rdCan < ID_RCV_NUM)
    {
        if(!CanMonitor_IsRepeatedRx(CAN1_RBuf))
        {
            CanMonitor_Handle(CAN1_RBuf, CAN_SendXLen);
        }
    }

#if CAN_MONITOR_ENABLE_CAN2
    rdCan = CAN2_Get_Data(MON_REQ_ID);
    if(rdCan < ID_RCV_NUM)
    {
        if(!CanMonitor_IsRepeatedRx(CAN2_RBuf))
        {
            CanMonitor_Handle(CAN2_RBuf, CAN2_SendXLen);
        }
    }

#endif
}
