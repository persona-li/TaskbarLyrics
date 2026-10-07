#pragma once
// Bounded RFC 1950/1951 reader. QRC uses a zlib stream followed by DES block padding.
#include <array>
#include <cstdint>
#include <stdexcept>
#include <string>
#include <vector>
namespace lyrics::qrc
{
class BitReader
{
    const std::vector<uint8_t> &bytes;
    size_t bit{};

  public:
    explicit BitReader(const std::vector<uint8_t> &b, size_t start = 0) : bytes(b), bit(start * 8)
    {
    }
    uint32_t read(unsigned count)
    {
        if (count > 24 || bit + count > bytes.size() * 8)
            throw std::runtime_error("Truncated DEFLATE");
        uint32_t value = 0;
        for (unsigned i = 0; i < count; ++i, ++bit)
            value |= ((bytes[bit / 8] >> (bit % 8)) & 1u) << i;
        return value;
    }
    void align()
    {
        bit = (bit + 7) & ~size_t(7);
    }
    size_t offset() const
    {
        return (bit + 7) / 8;
    }
};
struct Huffman
{
    std::array<std::vector<int>, 16> symbols;
    explicit Huffman(const std::vector<int> &lengths)
    {
        std::array<int, 16> count{}, next{};
        for (int length : lengths)
        {
            if (length < 0 || length > 15)
                throw std::runtime_error("Invalid Huffman length");
            ++count[length];
        }
        int code = 0;
        for (int i = 1; i <= 15; ++i)
        {
            code = (code + (i == 1 ? 0 : count[i - 1])) << 1;
            if (code + count[i] > (1 << i))
                throw std::runtime_error("Oversubscribed Huffman tree");
            next[i] = code;
            if (count[i])
                symbols[i].assign(size_t(1) << i, -1);
        }
        for (size_t i = 0; i < lengths.size(); ++i)
            if (lengths[i])
                symbols[lengths[i]][next[lengths[i]]++] = static_cast<int>(i);
    }
    int decode(BitReader &reader) const
    {
        unsigned code = 0;
        for (int length = 1; length <= 15; ++length)
        {
            code = (code << 1) | reader.read(1);
            if (!symbols[length].empty() && symbols[length][code] >= 0)
                return symbols[length][code];
        }
        throw std::runtime_error("Invalid Huffman symbol");
    }
};
inline std::string inflate(const std::vector<uint8_t> &bytes, bool zlib = true)
{
    if (zlib && (bytes.size() < 6 || (bytes[0] & 15) != 8 || (bytes[0] >> 4) > 7 ||
                 ((bytes[0] << 8) | bytes[1]) % 31 != 0 || (bytes[1] & 32)))
        throw std::runtime_error("Invalid zlib header");
    BitReader reader(bytes, zlib ? 2 : 0);
    std::string out;
    constexpr size_t limit = 8 * 1024 * 1024;
    constexpr int lengthBase[] = {3,  4,  5,  6,  7,  8,  9,  10, 11,  13,  15,  17,  19,  23, 27,
                                  31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258};
    constexpr int lengthBits[] = {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2,
                                  2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0};
    constexpr int distanceBase[] = {1,    2,    3,    4,    5,    7,    9,    13,    17,    25,
                                    33,   49,   65,   97,   129,  193,  257,  385,   513,   769,
                                    1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577};
    constexpr int distanceBits[] = {0, 0, 0, 0, 1, 1, 2, 2,  3,  3,  4,  4,  5,  5,  6,
                                    6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13};
    bool final = false;
    while (!final)
    {
        final = reader.read(1) != 0;
        int type = reader.read(2);
        if (type == 0)
        {
            reader.align();
            unsigned length = reader.read(16), complement = reader.read(16);
            if ((length ^ complement) != 65535 || out.size() + length > limit)
                throw std::runtime_error("Invalid stored DEFLATE block");
            for (unsigned i = 0; i < length; ++i)
                out += static_cast<char>(reader.read(8));
            continue;
        }
        std::vector<int> litLengths(288), distLengths(32);
        if (type == 1)
        {
            for (int i = 0; i < 288; ++i)
                litLengths[i] = i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8;
            std::fill(distLengths.begin(), distLengths.end(), 5);
        }
        else if (type == 2)
        {
            int nl = reader.read(5) + 257, nd = reader.read(5) + 1, nc = reader.read(4) + 4;
            if (nl > 286)
                throw std::runtime_error("Invalid literal count");
            constexpr int order[] = {16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15};
            std::vector<int> codeLengths(19);
            for (int i = 0; i < nc; ++i)
                codeLengths[order[i]] = reader.read(3);
            Huffman codes(codeLengths);
            std::vector<int> lengths;
            while (lengths.size() < static_cast<size_t>(nl + nd))
            {
                int symbol = codes.decode(reader), value = symbol, repeat = 1;
                if (symbol == 16)
                {
                    if (lengths.empty())
                        throw std::runtime_error("Invalid DEFLATE repeat");
                    value = lengths.back();
                    repeat = reader.read(2) + 3;
                }
                else if (symbol == 17)
                {
                    value = 0;
                    repeat = reader.read(3) + 3;
                }
                else if (symbol == 18)
                {
                    value = 0;
                    repeat = reader.read(7) + 11;
                }
                if (lengths.size() + repeat > static_cast<size_t>(nl + nd))
                    throw std::runtime_error("DEFLATE repeat overrun");
                lengths.insert(lengths.end(), repeat, value);
            }
            litLengths.assign(lengths.begin(), lengths.begin() + nl);
            distLengths.assign(lengths.begin() + nl, lengths.end());
        }
        else
            throw std::runtime_error("Reserved DEFLATE block");
        if (!litLengths[256])
            throw std::runtime_error("Missing DEFLATE end symbol");
        Huffman literals(litLengths), distances(distLengths);
        for (;;)
        {
            int symbol = literals.decode(reader);
            if (symbol == 256)
                break;
            if (symbol < 256)
            {
                if (out.size() == limit)
                    throw std::runtime_error("QRC expansion too large");
                out += static_cast<char>(symbol);
                continue;
            }
            if (symbol > 285)
                throw std::runtime_error("Invalid DEFLATE length");
            int length = lengthBase[symbol - 257] + reader.read(lengthBits[symbol - 257]);
            int distanceSymbol = distances.decode(reader);
            if (distanceSymbol >= 30)
                throw std::runtime_error("Invalid DEFLATE distance");
            size_t distance = distanceBase[distanceSymbol] + reader.read(distanceBits[distanceSymbol]);
            if (distance > out.size() || out.size() + length > limit)
                throw std::runtime_error("DEFLATE copy outside window");
            for (int i = 0; i < length; ++i)
                out += out[out.size() - distance];
        }
    }
    if (zlib)
    {
        size_t offset = reader.offset();
        if (offset + 4 > bytes.size())
            throw std::runtime_error("Missing Adler checksum");
        uint32_t a = 1, b = 0;
        for (unsigned char c : out)
        {
            a = (a + c) % 65521;
            b = (b + a) % 65521;
        }
        uint32_t expected = (uint32_t(bytes[offset]) << 24) | (uint32_t(bytes[offset + 1]) << 16) |
                            (uint32_t(bytes[offset + 2]) << 8) | bytes[offset + 3];
        if (expected != ((b << 16) | a))
            throw std::runtime_error("Invalid Adler checksum");
    }
    return out;
}
} // namespace lyrics::qrc
