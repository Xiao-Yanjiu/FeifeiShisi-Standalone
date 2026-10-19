#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdint.h>
#include <string.h>

/* A private fake game process: loads the bridge into itself, never targets FF14. */
#pragma pack(push,1)
typedef struct {uint32_t magic,timestamp,image_size;wchar_t pipe[192];uint32_t rva[13];unsigned char prologue[16],listing_prologue[16];uint32_t agent_index,proxy_index,agent_slot,info_slot;unsigned char reserved[16];} Args;
#pragma pack(pop)
typedef struct {char* data;int64_t size,used,length;} Utf8;
typedef uint32_t (*PrintFn)(void*,uint16_t,void*,void*,int,unsigned char);
static unsigned char state[256],manager[128],actor[10000],agent[14500],agent_module[256],info_module[7000],proxy[256];
static void* ui_table[64];static void** ui_vtable=ui_table;
static void* framework;
static volatile LONG completed,failed,dispatched,received;
static HANDLE ready;
static wchar_t pipe_path[256];
__declspec(noinline) uint32_t fake_print(void* self,uint16_t kind,void* sender,void* body,int timestamp,unsigned char silent) { (void)self;(void)kind;(void)sender;(void)body;(void)timestamp;(void)silent;return 0x12345678; }
static PrintFn volatile print_call=fake_print;
__declspec(noinline) void* fake_ui(void* value){return value;}
__declspec(noinline) void* fake_ctor(void* p){memset(p,0,104);return p;}
__declspec(noinline) void fake_set(void* p,const char* text){Utf8* s=p;s->data=(char*)text;s->size=s->used=(int64_t)strlen(text)+1;s->length=s->used-1;}
__declspec(noinline) void fake_dtor(void* p){memset(p,0,104);}
__declspec(noinline) void* fake_agent_module(void* p){(void)p;return agent_module;}
__declspec(noinline) void* fake_info_module(void* p){(void)p;return info_module;}
__declspec(noinline) void* fake_get_agent(void* p,uint32_t id){if(p!=agent_module||id!=130)InterlockedExchange(&failed,1);return agent;}
__declspec(noinline) void fake_receive(void* self,void* packet){if(self!=proxy)InterlockedExchange(&failed,1);uint64_t id=0;memcpy(&id,(unsigned char*)packet+32,8);if(id!=42)InterlockedExchange(&failed,1);InterlockedIncrement(&received);}
typedef void (*ReceiveFn)(void*,void*);static ReceiveFn volatile receive_call=fake_receive;
__declspec(noinline) unsigned char fake_request(void* p,unsigned char category){if(p!=agent||category!=0||agent[14009]!=2)InterlockedExchange(&failed,1);agent[14011]=category;return 0;}
__declspec(noinline) unsigned char fake_update(void* p){if(p!=agent)InterlockedExchange(&failed,1);unsigned char packet[1632]={0};uint64_t id=42;memcpy(packet+32,&id,8);uint16_t lifetime=1800;memcpy(packet+32+68,&lifetime,2);memcpy(packet+32+168,"Fixture leader",15);receive_call(proxy,packet);return 1;}
__declspec(noinline) void fake_chat(void* ui,void* str,intptr_t unused,unsigned char silent) {
    (void)ui;(void)unused;(void)silent;Utf8* s=str;
    if(strcmp(s->data,"/s native fixture")!=0){InterlockedExchange(&failed,1);return;}
    InterlockedIncrement(&dispatched);Utf8 sender={"Fixture",8,8,7};
    if(print_call(0,10,&sender,str,0,0)!=0x12345678)InterlockedExchange(&failed,1);
}
static int write_all(HANDLE p,const void* bytes,DWORD size){DWORD n=0;return WriteFile(p,bytes,size,&n,0)&&n==size;}
static int read_all(HANDLE p,void* bytes,DWORD size){unsigned char* data=bytes;while(size){DWORD n=0;if(!ReadFile(p,data,size,&n,0)||n==0)return 0;data+=n;size-=n;}return 1;}
static int send_command(HANDLE p,uint64_t id,uint64_t cid){char text[]="/s native fixture";unsigned char body[1024];uint32_t type=1,length=24+(uint32_t)strlen(text),n=(uint32_t)strlen(text);memcpy(body,&type,4);memcpy(body+4,&id,8);memcpy(body+12,&cid,8);memcpy(body+20,&n,4);memcpy(body+24,text,n);return write_all(p,&length,4)&&write_all(p,body,length);}
static int send_query(HANDLE p){unsigned char body[24]={0};uint32_t type=2,length=24,area=2;uint64_t id=3,cid=123;memcpy(body,&type,4);memcpy(body+4,&id,8);memcpy(body+12,&cid,8);memcpy(body+20,&area,4);return write_all(p,&length,4)&&write_all(p,body,length);}
static DWORD WINAPI server(void* unused){
    (void)unused;HANDLE p=CreateNamedPipeW(pipe_path,PIPE_ACCESS_DUPLEX,PIPE_TYPE_BYTE|PIPE_WAIT,1,65536,65536,0,0);if(p==INVALID_HANDLE_VALUE){InterlockedExchange(&failed,1);SetEvent(ready);return 0;}SetEvent(ready);
    if(!ConnectNamedPipe(p,0)&&GetLastError()!=ERROR_PIPE_CONNECTED){InterlockedExchange(&failed,1);CloseHandle(p);return 0;}
    int hello=0,chat=0,ack=0,wrong=0,states=0,recruit=0;char json[65537];
    while(!failed && (!wrong || !chat || !states || !recruit)){
        uint32_t size=0;if(!read_all(p,&size,4)||size>65536||!size||!read_all(p,json,size)){InterlockedExchange(&failed,1);break;}json[size]=0;
        if(strstr(json,"\"type\":\"hello\"")){hello++;if(!send_command(p,1,123)){InterlockedExchange(&failed,1);break;}}
        if(strstr(json,"\"type\":\"state\"") && strstr(json,"\"name\":\"Fixture\"") && strstr(json,"\"cid\":\"123\""))states++;
        if(strstr(json,"\"type\":\"chat\"") && strstr(json,"\"kind\":10"))chat++;
        if(strstr(json,"\"type\":\"ack\"")){
            if(strstr(json,"\"id\":1,") && strstr(json,"\"error\":\"\"")){ack++;if(!send_command(p,2,999))InterlockedExchange(&failed,1);}
            else if(strstr(json,"\"id\":2,") && strstr(json,"Character changed")){wrong++;if(!send_query(p))InterlockedExchange(&failed,1);}
            else InterlockedExchange(&failed,1);
        }
        if(strstr(json,"\"type\":\"recruit\"") && strstr(json,"\"id\":3,") && strstr(json,"\"count\":1,") && strstr(json,"\"done\":true") && strstr(json,"\"error\":\"\"")){recruit++;if(agent[14009]!=1||agent[14011]!=5)InterlockedExchange(&failed,1);}
    }
    if(hello!=1||ack!=1||wrong!=1||chat!=1||states<1||dispatched!=1||recruit!=1||received!=1)InterlockedExchange(&failed,1);
    CloseHandle(p);InterlockedExchange(&completed,1);return 0;
}
static LRESULT CALLBACK wndproc(HWND h,UINT m,WPARAM w,LPARAM l){return DefWindowProcW(h,m,w,l);}
int main(int argc,char** argv){
    if(argc!=2){fprintf(stderr,"DLL path required\n");return 2;}
    unsigned char* base=(unsigned char*)GetModuleHandleW(0);IMAGE_DOS_HEADER* dos=(IMAGE_DOS_HEADER*)base;IMAGE_NT_HEADERS64* pe=(IMAGE_NT_HEADERS64*)(base+dos->e_lfanew);
    state[0]=1;memcpy(state+1,"Fixture",8);uint64_t cid=123;memcpy(state+104,&cid,8);memcpy(actor+9048,&cid,8);unsigned char* a=actor;memcpy(manager+32,&a,8);framework=&ui_vtable;ui_table[37]=fake_agent_module;ui_table[35]=fake_info_module;void* proxy_address=proxy;memcpy(info_module+6520+20*8,&proxy_address,8);agent[14009]=1;agent[14011]=5;
    WNDCLASSW klass={0};klass.lpfnWndProc=wndproc;klass.hInstance=GetModuleHandleW(0);klass.lpszClassName=L"FFXIVGAME";RegisterClassW(&klass);
    HWND window=CreateWindowExW(0,L"FFXIVGAME",L"Native fixture only",0,0,0,100,100,0,0,klass.hInstance,0);if(!window)return 3;
    Args args={0};args.magic=0x54524932;args.timestamp=pe->FileHeader.TimeDateStamp;args.image_size=pe->OptionalHeader.SizeOfImage;args.agent_index=130;args.proxy_index=20;args.agent_slot=37;args.info_slot=35;
    swprintf(args.pipe,192,L"FF14Chat.Trial.fixture.%lu",GetCurrentProcessId());swprintf(pipe_path,256,L"\\\\.\\pipe\\%ls",args.pipe);
    void* ptrs[]={fake_print,&framework,fake_ui,fake_chat,fake_ctor,fake_set,fake_dtor,state,manager,fake_receive,fake_request,fake_get_agent,fake_update};for(unsigned i=0;i<13;i++)args.rva[i]=(uint32_t)((unsigned char*)ptrs[i]-base);memcpy(args.prologue,fake_print,16);memcpy(args.listing_prologue,fake_receive,16);
    HMODULE dll=LoadLibraryA(argv[1]);if(!dll){fprintf(stderr,"LoadLibrary failed %lu\n",GetLastError());return 4;}
    DWORD (WINAPI *start)(void*)=(void*)GetProcAddress(dll,"TrialStart");if(!start)return 5;
    args.timestamp++;if(start(&args)!=ERROR_REVISION_MISMATCH){fprintf(stderr,"Mismatched version was accepted\n");return 6;}args.timestamp--;
    for(int cycle=0;cycle<6;cycle++) {
    InterlockedExchange(&completed,0);InterlockedExchange(&failed,0);InterlockedExchange(&dispatched,0);InterlockedExchange(&received,0);
    ready=CreateEventW(0,TRUE,FALSE,0);HANDLE worker=CreateThread(0,0,server,0,0,0);WaitForSingleObject(ready,3000);
    DWORD result=start(&args);if(result){fprintf(stderr,"Start failed %lu\n",result);return 7;}
    ULONGLONG until=GetTickCount64()+12000;
    while(!completed && !failed && GetTickCount64()<until){MSG m;while(PeekMessageW(&m,0,0,0,PM_REMOVE)){TranslateMessage(&m);DispatchMessageW(&m);}Sleep(5);}
    for(int i=0;i<100;i++){MSG m;while(PeekMessageW(&m,0,0,0,PM_REMOVE)){TranslateMessage(&m);DispatchMessageW(&m);}Sleep(5);}
    if(!completed||failed){fprintf(stderr,"Native fixture FAILED (completed=%ld,failed=%ld,dispatched=%ld)\n",completed,failed,dispatched);return 8;}
    CloseHandle(worker);CloseHandle(ready);
    }
    DestroyWindow(window);printf("Native fixture passed: 6 connect/disconnect cycles; version guard, pipe handshake, main-thread chat, capture, character guard, recruitment refresh/fallback/capture, selection restore, original returns, teardown. No FF14 process was accessed.\n");return 0;
}
