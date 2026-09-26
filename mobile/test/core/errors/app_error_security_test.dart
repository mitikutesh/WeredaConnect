import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_exception.dart';

void main() {
  test('step-up and KYC rejections get their own codes', () {
    expect(
      mapErrorToCode(const StepUpRequiredException()),
      AppErrorCode.stepUpRequired,
    );
    expect(
      mapErrorToCode(const SyncRejectedException('AUTH_040: step-up required')),
      AppErrorCode.stepUpRequired,
    );
    expect(
      mapErrorToCode(
        const SyncRejectedException('ORDER_KYC_REQUIRED: Fayda needed'),
      ),
      AppErrorCode.kycRequired,
    );
    expect(
      mapErrorToCode(const SyncRejectedException('ORDER_UNAUTHORIZED: no')),
      AppErrorCode.server,
    );
  });

  test('message helpers match on the prefix only', () {
    expect(isStepUpRequiredMessage('AUTH_040: x'), isTrue);
    expect(isStepUpRequiredMessage('x AUTH_040'), isFalse);
    expect(isStepUpRequiredMessage(null), isFalse);
    expect(
      isKycRequiredMessage('ORDER_KYC_REQUIRED: phone not verified'),
      isTrue,
    );
    expect(isKycRequiredMessage('FRAUD_VELOCITY_EXCEEDED'), isFalse);
  });
}
