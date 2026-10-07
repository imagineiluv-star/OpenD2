// OpenD2-owned narrow C ABI. All archive opens used by the client are read-only.
#include <StormLib.h>
#include <cstdint>
#include <cstring>
#include <string>
#ifdef _WIN32
#define API extern "C" __declspec(dllexport)
static std::wstring path(const char* text)
{
	int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text, -1, nullptr, 0);
	if(size == 0) return {};
	std::wstring result(size, L'\0');
	MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text, -1, result.data(), size);
	return result;
}
#else
#define API extern "C" __attribute__((visibility("default")))
static std::string path(const char* text) { return text; }
#endif
static int failure()
{
	auto code = SErrGetLastError();
	if(code == ERROR_NO_MORE_FILES) return 18;
	if(code == ERROR_FILE_NOT_FOUND) return 2;
	return code ? static_cast<int>(code) : -1;
}
API int od2_abi_version() { return 1; }
API int od2_open(const char* name, HANDLE* archive)
{
	return SFileOpenArchive(path(name).c_str(), 0, MPQ_OPEN_READ_ONLY | MPQ_OPEN_CHECK_SECTOR_CRC, archive) ? 0 : failure();
}
API void od2_close(HANDLE archive) { SFileCloseArchive(archive); }
API int od2_file_open(HANDLE archive, const char* name, HANDLE* file, uint64_t* size)
{
	if(!SFileOpenFileEx(archive, name, SFILE_OPEN_FROM_MPQ, file)) return failure();
	DWORD high = 0;
	SErrSetLastError(0);
	DWORD low = SFileGetFileSize(*file, &high);
	if(low == SFILE_INVALID_SIZE && SErrGetLastError() != 0)
	{
		int error = failure(); SFileCloseFile(*file); *file = nullptr; return error;
	}
	*size = (static_cast<uint64_t>(high) << 32) | low;
	return 0;
}
API int od2_read(HANDLE file, void* buffer, uint32_t length, uint32_t* read)
{
	return SFileReadFile(file, buffer, length, read, nullptr) ? 0 : failure();
}
API void od2_file_close(HANDLE file) { SFileCloseFile(file); }
struct Find { HANDLE handle; SFILE_FIND_DATA data; };
API int od2_find_open(HANDLE archive, void** cursor)
{
	auto entry = new Find{};
	entry->handle = SFileFindFirstFile(archive, "*", &entry->data, nullptr);
	if(!entry->handle || entry->handle == reinterpret_cast<HANDLE>(static_cast<intptr_t>(-1))) { int error = failure(); delete entry; return error; }
	*cursor = entry; return 0;
}
API int od2_find_name(void* cursor, char* buffer, uint32_t capacity)
{
	auto entry = static_cast<Find*>(cursor);
	size_t size = strlen(entry->data.cFileName);
	if(size >= capacity) return ERROR_INSUFFICIENT_BUFFER;
	memcpy(buffer, entry->data.cFileName, size + 1); return 0;
}
API int od2_find_next(void* cursor)
{
	auto entry = static_cast<Find*>(cursor);
	return SFileFindNextFile(entry->handle, &entry->data) ? 0 : failure();
}
API void od2_find_close(void* cursor)
{
	auto entry = static_cast<Find*>(cursor);
	SFileFindClose(entry->handle); delete entry;
}
// Test fixture helper; callers supply a new, temporary filename.
// The managed production reader does not expose this entry point.
API int od2_fixture(const char* name, const char* logical, const void* data, uint32_t size, int listfile)
{
	HANDLE archive = nullptr, file = nullptr;
	if(!SFileCreateArchive(path(name).c_str(), listfile ? MPQ_CREATE_LISTFILE : 0, 16, &archive)) return failure();
	int error = 0;
	if(!SFileCreateFile(archive, logical, 0, size, 0, MPQ_FILE_COMPRESS | MPQ_FILE_ENCRYPTED | MPQ_FILE_SECTOR_CRC, &file)) error = failure();
	else
	{
		if(!SFileWriteFile(file, data, size, MPQ_COMPRESSION_ZLIB)) error = failure();
		if(!SFileFinishFile(file) && !error) error = failure();
	}
	if(!SFileCloseArchive(archive) && !error) error = failure();
	return error;
}
