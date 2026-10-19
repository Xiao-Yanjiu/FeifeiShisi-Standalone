#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "vendor/minhook/include/MinHook.h"

/* This DLL has no Dalamud/Omni dependencies. All calls are bound to a checked
   executable profile. Network, TLS, image capture and disk I/O stay in the EXE. */
#pragma pack(push,1)
typedef struct { uint32_t magic, timestamp, image_size; wchar_t pipe[192]; uint32_t rva[13]; unsigned char print_prologue[16],listing_prologue[16]; uint32_t agent_index,proxy_index,agent_slot,info_slot; uint32_t extra[4]; } StartArgs;
#pragma pack(pop)
_Static_assert(sizeof(StartArgs)==512,"host/native packet mismatch");
typedef uint32_t (*PrintMessageFn)(void*,uint16_t,void*,void*,int,unsigned char);
typedef void* (*GetUiFn)(void*);
typedef void (*ProcessChatFn)(void*,void*,intptr_t,unsigned char);
typedef void* (*CtorFn)(void*);
typedef void (*SetFn)(void*,const char*);
typedef void (*DtorFn)(void*);
static PrintMessageFn original_print;
typedef void (*ReceiveListingFn)(void*,void*);
static ReceiveListingFn original_listing;
static unsigned char *base;
static StartArgs args;
static volatile LONG running, connected, stopping, command_pending;
static SRWLOCK queue_lock=SRWLOCK_INIT;
static SRWLOCK command_lock=SRWLOCK_INIT;
static HWND game_window;
static UINT_PTR timer_id;
static HANDLE timer_stopped;
static HANDLE pipe_handle=INVALID_HANDLE_VALUE;
typedef struct { unsigned kind; unsigned sender_len,body_len; unsigned char sender[2048], body[8192]; } Chat;
static Chat chats[128];
static unsigned head,tail;
typedef struct { uint64_t id,cid,accepted; unsigned kind,area,length; char text[512]; } Command;
static Command next_command;
static uint64_t owned_listing,owned_cid,pending_since;static uint16_t owned_confirm;static char pending_action[96];
static uint64_t ack_id;
static char ack_error[12000];
static volatile LONG ack_ready;
static unsigned dropped;
static SRWLOCK recruit_lock=SRWLOCK_INIT;
static unsigned char listings[256][400];
static unsigned listing_count,listing_cursor;
static volatile LONG recruit_active,recruit_ready,recruit_dropped;
static uint64_t recruit_id,recruit_cid,recruit_started,recruit_last;
static void* recruit_agent;
static unsigned char recruit_original_area,recruit_original_category,recruit_area;
static char recruit_error[256];

static int copy_safe(const void* src,void* dst,SIZE_T size) {
    SIZE_T n=0;return src && ReadProcessMemory(GetCurrentProcess(),src,dst,size,&n) && n==size;
}
static uint64_t get64(const void* p) { uint64_t v=0;copy_safe(p,&v,8);return v; }
static int put_safe(void* p,const void* value,SIZE_T size){SIZE_T n=0;return p&&WriteProcessMemory(GetCurrentProcess(),p,value,size,&n)&&n==size;}
static unsigned string_bytes(void* value,unsigned char* out,unsigned limit) {
    struct { void* data;int64_t size,used,length; } s;
    if(!copy_safe(value,&s,sizeof(s)) || s.used<1 || s.used>limit || s.size<s.used)return 0;
    if(!copy_safe(s.data,out,(SIZE_T)s.used))return 0;
    return (unsigned)s.used-1;
}
static uint32_t capture_print(void* self,uint16_t kind,void* sender,void* body,int time,unsigned char silent) {
    unsigned k=kind&127;
    if(InterlockedCompareExchange(&connected,0,0) && (k>0 && k<128)) {
        if(TryAcquireSRWLockExclusive(&queue_lock)) {
            unsigned next=(head+1)%128;
            if(next!=tail) {
                Chat* c=&chats[head];c->kind=kind;
                c->sender_len=string_bytes(sender,c->sender,sizeof(c->sender));
                c->body_len=string_bytes(body,c->body,sizeof(c->body));
                if(c->body_len)head=next;
            } else dropped++;
            ReleaseSRWLockExclusive(&queue_lock);
        }
    }
    return original_print(self,kind,sender,body,time,silent);
}
static void capture_listing(void* self,void* packet_data) {
    if(InterlockedCompareExchange(&recruit_active,0,0)) {
        unsigned char payload[1616];
        if(copy_safe((unsigned char*)packet_data+16,payload,sizeof(payload))) {
            if(TryAcquireSRWLockExclusive(&recruit_lock)) {
                if(InterlockedCompareExchange(&recruit_active,0,0)) {
                    recruit_last=GetTickCount64();
                    for(unsigned row=0;row<4;row++) {
                        unsigned char* p=payload+16+row*400;uint64_t id=0;memcpy(&id,p,8);if(!id)continue;
                        int exists=0;for(unsigned i=0;i<listing_count;i++){uint64_t old=0;memcpy(&old,listings[i],8);if(old==id){memcpy(listings[i],p,400);exists=1;break;}}
                        if(!exists){if(listing_count<256)memcpy(listings[listing_count++],p,400);else InterlockedExchange(&recruit_dropped,1);}
                    }
                }
                ReleaseSRWLockExclusive(&recruit_lock);
            }else InterlockedExchange(&recruit_dropped,1);
        }else InterlockedExchange(&recruit_dropped,1);
    }
    original_listing(self,packet_data);
}
static int write_all(const void* data,DWORD size) {
    const unsigned char* p=data;
    while(size) { DWORD n=0;if(!WriteFile(pipe_handle,p,size,&n,0)||!n)return 0;p+=n;size-=n; }return 1;
}
static int packet(const char* json) { uint32_t n=(uint32_t)strlen(json);return write_all(&n,4)&&write_all(json,n); }
static int read_all(void* data,DWORD size) {
    unsigned char* p=data;while(size) {DWORD n=0;if(!ReadFile(pipe_handle,p,size,&n,0)||!n)return 0;p+=n;size-=n;}return 1;
}
static void b64(const unsigned char* in,unsigned length,char* out) {
    static const char table[]="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    unsigned i,j=0;for(i=0;i<length;i+=3) { unsigned n=((unsigned)in[i])<<16;
        if(i+1<length)n|=((unsigned)in[i+1])<<8;if(i+2<length)n|=in[i+2];
        out[j++]=table[(n>>18)&63];out[j++]=table[(n>>12)&63];out[j++]=i+1<length?table[(n>>6)&63]:'=';out[j++]=i+2<length?table[n&63]:'=';
    }out[j]=0;
}
static void json_escape(const char* text,char* out,size_t capacity) {
    size_t i=0;for(const unsigned char* p=(const unsigned char*)text;*p && i+7<capacity;p++) {
        unsigned char c=*p;if(c=='"'||c=='\\'){out[i++]='\\';out[i++]=c;}else if(c<32){i+=(size_t)snprintf(out+i,capacity-i,"\\u%04x",c);}else out[i++]=c;
    }out[i]=0;
}
static uint64_t character_id(void) { unsigned char loaded=0;if(!copy_safe(base+args.rva[7],&loaded,1)||!loaded)return 0;return get64(base+args.rva[7]+104); }
static void* ui_module(void){void* framework=(void*)(uintptr_t)get64(base+args.rva[1]);return framework?((GetUiFn)(base+args.rva[2]))(framework):0;}
static void* ui_getter(void* ui,unsigned index){uint64_t vt=get64(ui),fn=get64((void*)(uintptr_t)(vt+index*8));if(fn<(uint64_t)(uintptr_t)base+4096||fn>=(uint64_t)(uintptr_t)base+args.image_size)return 0;return ((GetUiFn)(uintptr_t)fn)(ui);}
static void finish_recruit(const char* error) {
    if(recruit_agent && character_id()==recruit_cid) {
        unsigned char area=255,category=255;
        if(copy_safe((unsigned char*)recruit_agent+14009,&area,1)&&copy_safe((unsigned char*)recruit_agent+14011,&category,1)&&area==recruit_area&&category==0) {
            put_safe((unsigned char*)recruit_agent+14009,&recruit_original_area,1);put_safe((unsigned char*)recruit_agent+14011,&recruit_original_category,1);
        }
    }
    AcquireSRWLockExclusive(&recruit_lock);InterlockedExchange(&recruit_active,0);snprintf(recruit_error,sizeof(recruit_error),"%s",error);listing_cursor=0;if(error[0])listing_count=0;InterlockedExchange(&recruit_ready,1);ReleaseSRWLockExclusive(&recruit_lock);
}
static void begin_recruit(Command* cmd) {
    recruit_id=cmd->id;recruit_cid=cmd->cid;recruit_agent=0;recruit_area=(unsigned char)cmd->area;
    AcquireSRWLockExclusive(&recruit_lock);listing_count=listing_cursor=0;recruit_last=0;recruit_error[0]=0;InterlockedExchange(&recruit_dropped,0);ReleaseSRWLockExclusive(&recruit_lock);
    void* ui=ui_module();void* agents=ui?ui_getter(ui,args.agent_slot):0;
    void* agent=agents?((void*(*)(void*,uint32_t))(base+args.rva[11]))(agents,args.agent_index):0;
    if(!agent){finish_recruit("Game recruitment agent is not ready");return;}
    recruit_agent=agent;
    if(!copy_safe((unsigned char*)agent+14009,&recruit_original_area,1)||!copy_safe((unsigned char*)agent+14011,&recruit_original_category,1)){finish_recruit("Recruitment layout could not be read");return;}
    void* proxy=(void*)(uintptr_t)get64((unsigned char*)agent+224);
    if(!proxy) {
        void* info=ui_getter(ui,args.info_slot);proxy=info?(void*)(uintptr_t)get64((unsigned char*)info+6520+args.proxy_index*8):0;
        if(!proxy||!put_safe((unsigned char*)agent+224,&proxy,8)){finish_recruit("Game recruitment data proxy is not ready");return;}
    }
    if(!put_safe((unsigned char*)agent+14009,&recruit_area,1)){finish_recruit("Recruitment selection could not be set");return;}
    recruit_started=GetTickCount64();InterlockedExchange(&recruit_active,1);
    unsigned char sent=((unsigned char(*)(void*,unsigned char))(base+args.rva[10]))(agent,0);
    if(!sent)((unsigned char(*)(void*))(base+args.rva[12]))(agent);
}
static void acknowledge(uint64_t id,const char* error) { ack_id=id;snprintf(ack_error,sizeof(ack_error),"%s",error);InterlockedExchange(&ack_ready,1); }

static void* addon(const char* name) {
    if(!args.extra[2]||!args.extra[3])return 0;
    void* stage=(void*)(uintptr_t)get64(base+args.extra[2]);
    void* manager=stage?(void*)(uintptr_t)get64((unsigned char*)stage+32):0;
    return manager?((void*(*)(void*,const char*,int))(base+args.extra[3]))(manager,name,1):0;
}
static int visible(void* unit){unsigned char ready=0;uint32_t flags=0;return unit&&copy_safe((unsigned char*)unit+417,&ready,1)&&copy_safe((unsigned char*)unit+408,&flags,4)&&(ready&1)&&(flags&(1u<<21));}
static int click_button(void* button){
    if(!button)return 0;void* node=(void*)(uintptr_t)get64((unsigned char*)button+168);uint16_t flags=0;
    if(!node||!copy_safe((unsigned char*)node+174,&flags,2)||(flags&0x30)!=0x30)return 0;
    void* event=(void*)(uintptr_t)get64((unsigned char*)node+24);
    for(unsigned i=0;event&&i<32;i++){
        unsigned char bytes[48];if(!copy_safe(event,bytes,48))return 0;
        if(bytes[40]==25){void* listener=(void*)(uintptr_t)get64(bytes+16);void* table=listener?(void*)(uintptr_t)get64(listener):0;void* receive=table?(void*)(uintptr_t)get64((unsigned char*)table+16):0;
            if(!receive||(unsigned char*)receive<base||(unsigned char*)receive>=base+args.image_size)return 0;
            uint32_t parameter=0;memcpy(&parameter,bytes+24,4);unsigned char data[40]={0};
            ((void(*)(void*,uint16_t,uint32_t,void*,void*))receive)(listener,25,parameter,event,data);return 1;
        }
        event=(void*)(uintptr_t)get64(bytes+32);
    }return 0;
}
static const char* native_action(Command* cmd,void* ui){
    unsigned set,index;unsigned long long id;char rest;
    if(sscanf(cmd->text,"macro:%u:%u%c",&set,&index,&rest)==2){if(set>1||index>99||!args.extra[0])return "Macro index invalid";int active=0;if(!copy_safe((unsigned char*)ui+760656+704,&active,4)||active>=0)return "Game is already executing a macro";((void(*)(void*,void*))(base+args.extra[0]))((unsigned char*)ui+760656,(unsigned char*)ui+25008+96+set*167200+index*1672);return "";}
    if(sscanf(cmd->text,"listing:%llu%c",&id,&rest)==1){if(!id||!args.extra[1])return "Listing invalid";void* table=(void*)(uintptr_t)get64(ui);void* agents=table?((void*(*)(void*))(uintptr_t)get64((unsigned char*)table+args.agent_slot*8))(ui):0;void* agent=agents?((void*(*)(void*,uint32_t))(base+args.rva[11]))(agents,args.agent_index):0;if(!agent)return "Recruitment UI unavailable";
        if(!((unsigned char(*)(void*,uint64_t))(base+args.extra[1]))(agent,(uint64_t)id))return "Game rejected listing";recruit_agent=agent;owned_listing=(uint64_t)id;owned_cid=cmd->cid;return "";}
    if(!strncmp(cmd->text,"recruit:",8)){
        if(owned_cid!=cmd->cid||!owned_listing)return "Recruitment session changed";void* detail=addon("LookingForGroupDetail");if(!visible(detail))return "Recruitment detail not ready";
        void* table=(void*)(uintptr_t)get64(ui);void* agents=table?((void*(*)(void*))(uintptr_t)get64((unsigned char*)table+args.agent_slot*8))(ui):0;void* agent=agents?((void*(*)(void*,uint32_t))(base+args.rva[11]))(agents,args.agent_index):0;
        if(!agent||get64((unsigned char*)agent+11424)!=owned_listing)return "Recruitment selection changed";
        unsigned offset=0;if(!strcmp(cmd->text+8,"join"))offset=608;else if(!strcmp(cmd->text+8,"tell"))offset=664;else if(sscanf(cmd->text+8,"join:%u%c",&index,&rest)==1&&index<6)offset=616+index*8;else return "Invalid recruitment action";
        if(visible(addon("SelectYesno")))return "A game confirmation is already open";
        if(!click_button((void*)(uintptr_t)get64((unsigned char*)detail+offset)))return "Game button unavailable";
        strcpy(pending_action,"recruit");owned_confirm=0;pending_since=GetTickCount64();return "";
    }
    if(sscanf(cmd->text,"confirm:%u:%u%c",&set,&index,&rest)==2){
        if(set>1||index==0||index!=owned_confirm||owned_cid!=cmd->cid||GetTickCount64()-pending_since>30000)return "Confirmation changed";
        void* confirm=addon("SelectYesno");uint16_t current=0;if(!visible(confirm)||!copy_safe((unsigned char*)confirm+484,&current,2)||current!=owned_confirm)return "Confirmation changed";
        if(!click_button((void*)(uintptr_t)get64((unsigned char*)confirm+(set?576:584))))return "Confirmation button unavailable";
        owned_confirm=0;pending_action[0]=0;return "";
    }
    return "Unknown native action";
}
static int valid_chat(const char* s) {
    const char* prefixes[]={"/s ","/sh ","/tell ","/p ","/a ","/fc ","/b ","/y ","/l1 ","/l2 ","/l3 ","/l4 ","/l5 ","/l6 ","/l7 ","/l8 ","/cwl1 ","/cwl2 ","/cwl3 ","/cwl4 ","/cwl5 ","/cwl6 ","/cwl7 ","/cwl8 "};
    for(unsigned i=0;i<sizeof(prefixes)/sizeof(prefixes[0]);i++)if(strncmp(s,prefixes[i],strlen(prefixes[i]))==0)return 1;return 0;
}
static VOID CALLBACK game_tick(HWND window,UINT message,UINT_PTR id,DWORD time) {
    (void)message;(void)time;
    if(InterlockedCompareExchange(&stopping,0,0)) {if(InterlockedCompareExchange(&recruit_active,0,0))finish_recruit("Connection closed");KillTimer(window,id);timer_id=0;SetEvent(timer_stopped);return;}
    if(pending_action[0]&&owned_cid==character_id()&&GetTickCount64()-pending_since<30000){void* confirm=addon("SelectYesno");if(visible(confirm)&&!owned_confirm){uint16_t id=0;copy_safe((unsigned char*)confirm+484,&id,2);int relevant=1;if(!strcmp(pending_action,"recruit")){uint32_t expected=0;relevant=recruit_agent&&copy_safe((unsigned char*)recruit_agent+13880,&expected,4)&&expected==id;}if(relevant)owned_confirm=id;}}
    if(InterlockedCompareExchange(&recruit_active,0,0)) {
        uint64_t last;AcquireSRWLockShared(&recruit_lock);last=recruit_last;ReleaseSRWLockShared(&recruit_lock);
        unsigned char area=255;copy_safe((unsigned char*)recruit_agent+14009,&area,1);
        if(character_id()!=recruit_cid)finish_recruit("Character changed; refresh was cancelled");
        else if(area!=recruit_area)finish_recruit("Recruitment selection changed in game; refresh was cancelled");
        else if(InterlockedCompareExchange(&recruit_dropped,0,0))finish_recruit("Recruitment batch was incomplete; previous results must be retained");
        else if(last&&GetTickCount64()-last>=400)finish_recruit("");
        else if(GetTickCount64()-recruit_started>5500)finish_recruit("No recruitment response received; previous results must be retained");
        return;
    }
    if(!InterlockedCompareExchange(&command_pending,0,0)||!InterlockedCompareExchange(&connected,0,0))return;
    Command cmd;
    if(!TryAcquireSRWLockExclusive(&command_lock))return;
    cmd=next_command;InterlockedExchange(&command_pending,0);ReleaseSRWLockExclusive(&command_lock);
    if(GetTickCount64()-cmd.accepted>2500){acknowledge(cmd.id,"Chat request expired before dispatch");return;}
    if(!cmd.cid || character_id()!=cmd.cid){acknowledge(cmd.id,"Character changed; message was not dispatched");return;}
    if(cmd.kind==2){begin_recruit(&cmd);return;}
    void* framework=(void*)(uintptr_t)get64(base+args.rva[1]);
    if(!framework){acknowledge(cmd.id,"Game framework is not ready");return;}
    void* ui=((GetUiFn)(base+args.rva[2]))(framework);
    if(!ui){acknowledge(cmd.id,"Game UI is not ready");return;}
    if(cmd.kind==5){
        void* config=(unsigned char*)ui+595096;void* table=(void*)(uintptr_t)get64(config);void* size_fn=table?(void*)(uintptr_t)get64((unsigned char*)table+48):0;void* write_fn=table?(void*)(uintptr_t)get64((unsigned char*)table+16):0;
        if(!size_fn||!write_fn||(unsigned char*)size_fn<base||(unsigned char*)size_fn>=base+args.image_size||(unsigned char*)write_fn<base||(unsigned char*)write_fn>=base+args.image_size){acknowledge(cmd.id,"Native filter interface unavailable");return;}
        uint32_t size=((uint32_t(*)(void*))size_fn)(config);if(size<128||size>8192){char reason[128];snprintf(reason,sizeof(reason),"Native filter capacity unsupported: %u",size);acknowledge(cmd.id,reason);return;}
        unsigned char* bytes=HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,size);if(!bytes){acknowledge(cmd.id,"Native filter allocation failed");return;}char encoded[11000],result[11020];uint32_t length=((uint32_t(*)(void*,void*,uint32_t))write_fn)(config,bytes,size);
        if(length==0||length>size){HeapFree(GetProcessHeap(),0,bytes);acknowledge(cmd.id,"Native filter length changed");return;}b64(bytes,length,encoded);HeapFree(GetProcessHeap(),0,bytes);snprintf(result,sizeof(result),"filters:%s",encoded);acknowledge(cmd.id,result);return;
    }
    if(cmd.kind==4){acknowledge(cmd.id,native_action(&cmd,ui));return;}
    if(cmd.kind==3&&strncmp(cmd.text,"/pcmd ",6)==0){if(visible(addon("SelectYesno"))){acknowledge(cmd.id,"A game confirmation is already open");return;}strncpy(pending_action,cmd.text,sizeof(pending_action)-1);owned_cid=cmd.cid;owned_confirm=0;pending_since=GetTickCount64();}
    unsigned char utf8[112];memset(utf8,0,sizeof(utf8));
    ((CtorFn)(base+args.rva[4]))(utf8);
    ((SetFn)(base+args.rva[5]))(utf8,cmd.text);
    ((ProcessChatFn)(base+args.rva[3]))(ui,utf8,0,0);
    ((DtorFn)(base+args.rva[6]))(utf8);
    acknowledge(cmd.id,"");
}
static BOOL CALLBACK find_window(HWND window,LPARAM value) {
    DWORD pid=0;GetWindowThreadProcessId(window,&pid);wchar_t name[128];
    if(pid==GetCurrentProcessId() && GetClassNameW(window,name,128)>0 && wcscmp(name,L"FFXIVGAME")==0) {*(HWND*)value=window;return FALSE;}return TRUE;
}
static int send_state(void) {
    uint64_t cid=character_id();char name[65]={0},escaped[400],json[1200];unsigned char job=0,level=0;uint16_t world=0;uint32_t hp=0,maxhp=0;
    if(cid) {
        copy_safe(base+args.rva[7]+1,name,64);name[64]=0;
        void* actor=(void*)(uintptr_t)get64(base+args.rva[8]+32);
        if(actor && get64((unsigned char*)actor+9048)==cid) {
            copy_safe((unsigned char*)actor+9058,&world,2);copy_safe((unsigned char*)actor+458,&job,1);copy_safe((unsigned char*)actor+459,&level,1);
            copy_safe((unsigned char*)actor+428,&hp,4);copy_safe((unsigned char*)actor+432,&maxhp,4);
        }
    }
    json_escape(name,escaped,sizeof(escaped));
    snprintf(json,sizeof(json),"{\"type\":\"state\",\"pid\":%lu,\"online\":%s,\"name\":\"%s\",\"world\":\"%u\",\"cid\":\"%llu\",\"job\":%u,\"level\":%u,\"hp\":%u,\"maxHp\":%u,\"dropped\":%u}",GetCurrentProcessId(),cid?"true":"false",escaped,world,(unsigned long long)cid,job,level,hp,maxhp,dropped);
    return packet(json);
}
static DWORD WINAPI bridge_worker(void* unused) {
    (void)unused;wchar_t pipe_path[256];swprintf(pipe_path,256,L"\\\\.\\pipe\\%ls",args.pipe);
    if(!WaitNamedPipeW(pipe_path,10000))goto finish;
    pipe_handle=CreateFileW(pipe_path,GENERIC_READ|GENERIC_WRITE,0,0,OPEN_EXISTING,0,0);
    if(pipe_handle==INVALID_HANDLE_VALUE)goto finish;
    EnumWindows(find_window,(LPARAM)&game_window);
    if(!game_window){packet("{\"type\":\"hello\",\"protocol\":1,\"pid\":0,\"error\":\"Game window not found\"}");goto finish;}
    if(!original_print && (MH_Initialize()!=MH_OK || MH_CreateHook(base+args.rva[0],capture_print,(void**)&original_print)!=MH_OK)) {packet("{\"type\":\"hello\",\"protocol\":1,\"pid\":0,\"error\":\"Chat hook could not be prepared\"}");goto finish;}
    if(!original_listing && MH_CreateHook(base+args.rva[9],capture_listing,(void**)&original_listing)!=MH_OK)goto finish;
    timer_stopped=CreateEventW(0,TRUE,FALSE,0);
    timer_id=SetTimer(game_window,0,50,game_tick);
    if(!timer_stopped || !timer_id || MH_EnableHook(base+args.rva[0])!=MH_OK || MH_EnableHook(base+args.rva[9])!=MH_OK)goto finish;
    char hello[200];snprintf(hello,sizeof(hello),"{\"type\":\"hello\",\"protocol\":1,\"pid\":%lu}",GetCurrentProcessId());
    if(!packet(hello))goto finish;InterlockedExchange(&connected,1);
    ULONGLONG last_state=0;
    while(!InterlockedCompareExchange(&stopping,0,0)) {
        DWORD available=0;if(!PeekNamedPipe(pipe_handle,0,0,0,&available,0))break;
        if(available>=4) {
            uint32_t length=0;unsigned char data[1024];
            if(!read_all(&length,4)||length<24||length>sizeof(data)||!read_all(data,length))break;
            uint32_t command;uint64_t request,cid;unsigned text_length;
            memcpy(&command,data,4);memcpy(&request,data+4,8);memcpy(&cid,data+12,8);memcpy(&text_length,data+20,4);
            char text[512]={0};
            if(command==1||command==3||command==4||command==5){if(text_length==0||text_length>500||text_length+24!=length||memchr(data+24,0,text_length))break;memcpy(text,data+24,text_length);int invalid=0;for(unsigned i=0;i<text_length;i++)if((unsigned char)text[i]<32)invalid=1;if(invalid||(command==1&&!valid_chat(text))||(command==3&&text[0]!='/'))break;}
            else if(command!=2||length!=24||text_length>2)break;
            if(InterlockedCompareExchange(&command_pending,0,0)||InterlockedCompareExchange(&ack_ready,0,0)||InterlockedCompareExchange(&recruit_active,0,0)||InterlockedCompareExchange(&recruit_ready,0,0)) {break;}
            else {AcquireSRWLockExclusive(&command_lock);next_command.kind=command;next_command.area=text_length;next_command.id=request;next_command.cid=cid;next_command.accepted=GetTickCount64();next_command.length=command!=2?text_length:0;memcpy(next_command.text,text,command!=2?text_length+1:1);InterlockedExchange(&command_pending,1);ReleaseSRWLockExclusive(&command_lock);}
        }
        if(InterlockedCompareExchange(&ack_ready,0,0)) {char error[16000],json[18000];json_escape(ack_error,error,sizeof(error));snprintf(json,sizeof(json),"{\"type\":\"ack\",\"id\":%llu,\"error\":\"%s\"}",(unsigned long long)ack_id,error);if(!packet(json))break;InterlockedExchange(&ack_ready,0);}
        if(InterlockedCompareExchange(&recruit_ready,0,0)) {
            unsigned char raw[16*400];unsigned count=0;int done=0;char error[1600],encoded[8600],json[10600];
            AcquireSRWLockExclusive(&recruit_lock);while(listing_cursor<listing_count&&count<16){memcpy(raw+count*400,listings[listing_cursor++],400);count++;}done=listing_cursor==listing_count;json_escape(recruit_error,error,sizeof(error));if(done)InterlockedExchange(&recruit_ready,0);ReleaseSRWLockExclusive(&recruit_lock);
            b64(raw,count*400,encoded);snprintf(json,sizeof(json),"{\"type\":\"recruit\",\"id\":%llu,\"count\":%u,\"data\":\"%s\",\"done\":%s,\"error\":\"%s\"}",(unsigned long long)recruit_id,count,encoded,done?"true":"false",error);if(!packet(json))break;
        }
        Chat copy;int has=0;
        AcquireSRWLockExclusive(&queue_lock);if(head!=tail){copy=chats[tail];tail=(tail+1)%128;has=1;}ReleaseSRWLockExclusive(&queue_lock);
        if(has){char sender[2800],body[11000],json[14200];b64(copy.sender,copy.sender_len,sender);b64(copy.body,copy.body_len,body);snprintf(json,sizeof(json),"{\"type\":\"chat\",\"kind\":%u,\"sender\":\"%s\",\"body\":\"%s\"}",copy.kind,sender,body);if(!packet(json))break;}
        if(GetTickCount64()-last_state>500){if(!send_state())break;last_state=GetTickCount64();}
        if(!has)Sleep(10);
    }
finish:
    InterlockedExchange(&connected,0);InterlockedExchange(&stopping,1);
    if(original_print)MH_DisableHook(base+args.rva[0]);
    if(original_listing)MH_DisableHook(base+args.rva[9]);
    DWORD stopped=WAIT_OBJECT_0;
    if(timer_id && timer_stopped)stopped=WaitForSingleObject(timer_stopped,2000);
    /* Do not unload the DLL: a delayed WM_TIMER callback must never target freed code. */
    if(pipe_handle!=INVALID_HANDLE_VALUE){CloseHandle(pipe_handle);pipe_handle=INVALID_HANDLE_VALUE;}
    /* Keep the trampoline and DLL loaded. Reconnect only after the old timer is gone. */
    if(stopped==WAIT_OBJECT_0) {
        if(timer_stopped){CloseHandle(timer_stopped);timer_stopped=0;}
        InterlockedExchange(&command_pending,0);InterlockedExchange(&ack_ready,0);
        InterlockedExchange(&recruit_active,0);InterlockedExchange(&recruit_ready,0);
        AcquireSRWLockExclusive(&queue_lock);head=tail=0;ReleaseSRWLockExclusive(&queue_lock);
        InterlockedExchange(&running,0);
    }
    return 0;
}
__declspec(dllexport) DWORD WINAPI TrialStart(void* parameter) {
    if(InterlockedCompareExchange(&running,1,0))return ERROR_ALREADY_EXISTS;
    InterlockedExchange(&stopping,0);
    if(!copy_safe(parameter,&args,sizeof(args))||args.magic!=0x54524932 || args.pipe[191]!=0 || wcsncmp(args.pipe,L"FF14Chat.Trial.",15)!=0 || args.agent_index!=130 || args.proxy_index!=20 || args.agent_slot!=37 || args.info_slot!=35){InterlockedExchange(&running,0);return ERROR_INVALID_PARAMETER;}
    base=(unsigned char*)GetModuleHandleW(0);
    IMAGE_DOS_HEADER dos;IMAGE_NT_HEADERS64 pe;
    if(!copy_safe(base,&dos,sizeof(dos)) || dos.e_magic!=IMAGE_DOS_SIGNATURE || dos.e_lfanew<0 || !copy_safe(base+dos.e_lfanew,&pe,sizeof(pe)) || pe.Signature!=IMAGE_NT_SIGNATURE || pe.FileHeader.TimeDateStamp!=args.timestamp || pe.OptionalHeader.SizeOfImage!=args.image_size){InterlockedExchange(&running,0);return ERROR_REVISION_MISMATCH;}
    for(unsigned i=0;i<4;i++)if(args.extra[i]&&(args.extra[i]<0x1000||args.extra[i]>=args.image_size-256)){InterlockedExchange(&running,0);return ERROR_INVALID_ADDRESS;}
    for(unsigned i=0;i<13;i++)if(args.rva[i]<0x1000 || args.rva[i]>=args.image_size-256){InterlockedExchange(&running,0);return ERROR_INVALID_ADDRESS;}
    unsigned char original[16];if(!copy_safe(base+args.rva[0],original,16)||memcmp(original,args.print_prologue,16)){InterlockedExchange(&running,0);return ERROR_INVALID_DATA;}
    if(!copy_safe(base+args.rva[9],original,16)||memcmp(original,args.listing_prologue,16)){InterlockedExchange(&running,0);return ERROR_INVALID_DATA;}
    HANDLE thread=CreateThread(0,0,bridge_worker,0,0,0);if(!thread){DWORD error=GetLastError();InterlockedExchange(&running,0);return error;}CloseHandle(thread);return 0;
}
BOOL WINAPI DllMain(HINSTANCE module,DWORD reason,LPVOID reserved) { (void)reserved;if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(module);return TRUE; }
