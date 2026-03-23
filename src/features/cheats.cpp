#include <thread>
#include <chrono>
#include <Windows.h>
#include <unordered_set>
#include <unordered_map>
#include <cmath>
#include <cstdint>

#include "cheats.h"
#include "../offsets/offsets.h"
#include "../MemoryWriter/MemoryWriter.h"

static void speedHack() noexcept;
static void multiHack() noexcept;
static void pullHack() noexcept;

void cheats::hackThread() noexcept
{
    using namespace offsets;

    if (bSpeedHackActive) speedHack();
    if (bAoeHackActive) multiHack();
    if (bPullActive) pullHack();
}

static void speedHack() noexcept
{
    static auto lastWrite = std::chrono::steady_clock::now();

    const auto now = std::chrono::steady_clock::now();
    const auto elapsedMs = std::chrono::duration_cast<std::chrono::milliseconds>(now - lastWrite).count();
    if (elapsedMs < offsets::speedIntervalMs)
        return;

    offsets::speedAddress = (WORD*)MemoryWriter::FindDmaAddy(3, offsets::speedOffset, offsets::baseAddressA940);

    if (offsets::speedAddress != nullptr)
        *offsets::speedAddress = offsets::speedValue;

    lastWrite = now;
}

static void multiHack() noexcept
{
    using clock = std::chrono::steady_clock;

    static auto lastWrite = clock::now();
    static auto lastResolve = clock::time_point{};

    const auto now = clock::now();

    const float intervalMs = (offsets::multiIntervalMs < 0.0f) ? 0.0f : offsets::multiIntervalMs;
    const auto intervalUs = static_cast<long long>(intervalMs * 1000.0f);
    if (intervalUs > 0)
    {
        const auto elapsedUs = std::chrono::duration_cast<std::chrono::microseconds>(now - lastWrite).count();
        if (elapsedUs < intervalUs)
            return;
    }

    const bool shouldRefreshAddr =
        !offsets::aoeAddress1 ||
        !offsets::aoeAddress2 ||
        (std::chrono::duration_cast<std::chrono::milliseconds>(now - lastResolve).count() >= 500);

    if (shouldRefreshAddr)
    {
        offsets::aoeAddress1 = (WORD*)MemoryWriter::FindDmaAddy(3, offsets::aoeOffset1, offsets::baseAddressAA68);
        offsets::aoeAddress2 = (WORD*)MemoryWriter::FindDmaAddy(3, offsets::aoeOffset2, offsets::baseAddressAA68);
        lastResolve = now;
    }

    if (offsets::aoeAddress1 && offsets::aoeAddress2)
    {
        int burstCount = (offsets::multiBurstCount < 1) ? 1 : (offsets::multiBurstCount > 20 ? 20 : offsets::multiBurstCount);
        for (int i = 0; i < burstCount; ++i)
        {
            *offsets::aoeAddress1 = 65535;
            *offsets::aoeAddress2 = 65535;
        }
    }

    lastWrite = now;
}

static void pullHack() noexcept
{
    static std::unordered_set<uintptr_t> entities;

    const auto now = GetTickCount64();
    const auto ent = offsets::entityBuffer;
    if (ent)
        entities.insert(ent);

    const uintptr_t playerOffsets[] = { 0x158, 0x290 };
    const auto playerAddr = MemoryWriter::FindDmaAddy(2, playerOffsets, offsets::baseAddressAA68);
    if (!playerAddr)
        return;

    const auto player = *reinterpret_cast<uintptr_t*>(playerAddr);
    if (!player)
        return;

    const float px = *reinterpret_cast<float*>(player + 0x98);
    const float py = *reinterpret_cast<float*>(player + 0x9C);
    const float pz = *reinterpret_cast<float*>(player + 0xA0);

    const float strength = (offsets::pullStrength < 0.0f) ? 0.0f
        : (offsets::pullStrength > 1.0f ? 1.0f : offsets::pullStrength);

    const auto pullTowardPlayer = [&](uintptr_t e) noexcept
    {
        float& ex = *reinterpret_cast<float*>(e + 0x98);
        float& ey = *reinterpret_cast<float*>(e + 0x9C);
        float& ez = *reinterpret_cast<float*>(e + 0xA0);

        ex = ex + (px - ex) * strength;
        ey = ey + (py - ey) * strength;
        ez = ez + (pz - ez) * strength;
    };

    for (auto it = entities.begin(); it != entities.end(); )
    {
        const auto e = *it;
        if (e == player)
        {
            ++it;
            continue;
        }

        const float ex = *reinterpret_cast<float*>(e + 0x98);
        const float ey = *reinterpret_cast<float*>(e + 0x9C);
        const float ez = *reinterpret_cast<float*>(e + 0xA0);

        const float dx = ex - px;
        const float dy = ey - py;
        const float dz = ez - pz;
        const float dist = std::sqrt(dx * dx + dy * dy + dz * dz);

        if (dist < offsets::pullMaxDist && std::fabs(ey - py) < 5.0f)
            pullTowardPlayer(e);

        ++it;
    }
}
