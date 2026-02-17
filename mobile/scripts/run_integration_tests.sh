#!/bin/bash

# Run integration tests for WeredaConnect mobile app
#
# This script runs the offline sync integration tests and captures
# performance metrics for documentation.
#
# Usage:
#   ./scripts/run_integration_tests.sh [test_file]
#
# Examples:
#   ./scripts/run_integration_tests.sh                                    # Run all integration tests
#   ./scripts/run_integration_tests.sh sync_crop_listing_test.dart       # Run specific test

set -e  # Exit on error

# Colors for output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

# Default test file
TEST_FILE="${1:-sync_crop_listing_test.dart}"

echo -e "${GREEN}🧪 WeredaConnect Integration Tests${NC}"
echo "======================================"
echo ""

# Check if flutter is installed
if ! command -v flutter &> /dev/null; then
    echo -e "${RED}❌ Flutter is not installed or not in PATH${NC}"
    exit 1
fi

# Check if we're in the mobile directory
if [ ! -f "pubspec.yaml" ]; then
    echo -e "${RED}❌ Please run this script from the mobile directory${NC}"
    exit 1
fi

# Verify test file exists
if [ ! -f "integration_test/$TEST_FILE" ]; then
    echo -e "${RED}❌ Test file not found: integration_test/$TEST_FILE${NC}"
    exit 1
fi

echo -e "${YELLOW}📦 Getting dependencies...${NC}"
flutter pub get

echo ""
echo -e "${YELLOW}🔍 Analyzing test file...${NC}"
flutter analyze "integration_test/$TEST_FILE" || true

echo ""
echo -e "${YELLOW}🧪 Running integration tests...${NC}"
echo "Test file: integration_test/$TEST_FILE"
echo "Backend: $(grep -A 1 'apiBaseUrl:' lib/core/config/environment.dart | grep 'defaultValue:' | cut -d "'" -f 2 || echo 'Not found')"
echo ""

# Run tests and capture output
if flutter test "integration_test/$TEST_FILE" 2>&1 | tee test_output.log; then
    echo ""
    echo -e "${GREEN}✅ All integration tests passed!${NC}"

    # Extract performance metrics from output
    echo ""
    echo -e "${GREEN}📊 Performance Metrics Summary:${NC}"
    grep -E "Performance Metrics|Sync latency|Average latency|Total sync time|Success rate" test_output.log || echo "No metrics found in output"

    # Clean up
    rm -f test_output.log

    exit 0
else
    echo ""
    echo -e "${RED}❌ Integration tests failed!${NC}"

    # Show relevant error info
    echo ""
    echo -e "${YELLOW}Error details:${NC}"
    tail -20 test_output.log

    # Clean up
    rm -f test_output.log

    exit 1
fi
