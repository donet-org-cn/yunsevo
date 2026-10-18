#pragma once
#include <array>
#include <memory>
#include <string>
#include <vector>
struct DepthJob {
 int width, height, planes, sources;
 float nearDepth, farDepth, focal, cx, cy;
 // source image dimensions equal reference; each transform maps reference-camera to source-camera.
 std::vector<std::array<float,16>> transforms;
 std::vector<float> pixels; // reference, source1, source2, ... normalized grayscale
};
std::vector<float> cpuDepth(const DepthJob& job);
class VulkanDepth {
 struct Impl; std::unique_ptr<Impl> impl;
public:
 VulkanDepth(); ~VulkanDepth();
 std::string name() const;
 std::vector<float> run(const DepthJob& job);
};
