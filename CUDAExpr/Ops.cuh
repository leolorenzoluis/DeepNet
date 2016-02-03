#pragma once

#include "NDArray.cuh"


struct ConstOneElementwiseOp_t
{
	_dev float operator() (const float a)
	{
		return 1.0f;
	}
};


struct NegateElementwiseOp_t
{
	_dev float operator() (const float a)
	{
		return -a;
	}
};


struct LogElementwiseOp_t
{
	_dev float operator() (const float a)
	{
		return logf(a);
	}
};


struct ExpElementwiseOp_t
{
	_dev float operator() (const float a)
	{
		return expf(a);
	}
};



struct AddBinaryElementwiseOp_t
{
	_dev float operator() (const float a, const float b)
	{
		return a + b;
	}
};

struct SubstractBinaryElementwiseOp_t
{
	_dev float operator() (const float a, const float b)
	{
		return a - b;
	}
};

struct MultiplyBinaryElementwiseOp_t
{
	_dev float operator() (const float a, const float b)
	{
		return a * b;
	}
};

struct DivideBinaryElementwiseOp_t
{
	_dev float operator() (const float a, const float b)
	{
		return a / b;
	}
};


template <typename TUnaryElementwiseOp, typename TTarget, typename TA>
__global__ void elementwiseUnary(TTarget *target, const TA *a)
{
	TUnaryElementwiseOp op;
	size_t pos0 = threadIdx.x + blockIdx.x * blockDim.x;
	size_t pos1 = threadIdx.y + blockIdx.y * blockDim.y;
	size_t pos2 = threadIdx.z + blockIdx.z * blockDim.z;
	if (!(pos0 < target->shape(0) && pos1 < target->shape(1) && pos2 < target->shape(2)))
		return;
	target->element(pos0, pos1, pos2) = op(a->element(pos0, pos1, pos2));
}



template <typename TBinaryElementwiseOp, typename TTarget, typename TA, typename TB>
__global__ void elementwiseBinary(TTarget *target, const TA *a, const TB *b)
{
	TBinaryElementwiseOp op;
	size_t pos0 = threadIdx.x + blockIdx.x * blockDim.x;
	size_t pos1 = threadIdx.y + blockIdx.y * blockDim.y;
	size_t pos2 = threadIdx.z + blockIdx.z * blockDim.z;
	if (!(pos0 < target->shape(0) && pos1 < target->shape(1) && pos2 < target->shape(2)))
		return;
	target->element(pos0, pos1, pos2) = op(a->element(pos0, pos1, pos2), 
										   b->element(pos0, pos1, pos2));
}

