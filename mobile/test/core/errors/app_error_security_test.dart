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

  test('a Fayda step-up (AUTH_041, above 10,000 ETB) is its own code', () {
    expect(
      mapErrorToCode(
        const StepUpRequiredException('Fayda', 403, faydaStepUpRequiredCode),
      ),
      AppErrorCode.faydaStepUpRequired,
    );
    expect(
      mapErrorToCode(
        const SyncRejectedException('AUTH_041: Confirm it is you with Fayda'),
      ),
      AppErrorCode.faydaStepUpRequired,
    );
    expect(const StepUpRequiredException().requiresFayda, isFalse);
  });

  test('message helpers match on the prefix only', () {
    expect(isStepUpRequiredMessage('AUTH_040: x'), isTrue);
    expect(isStepUpRequiredMessage('x AUTH_040'), isFalse);
    expect(isStepUpRequiredMessage(null), isFalse);
    // Both step-up kinds keep an op pending; only AUTH_041 needs Fayda.
    expect(isStepUpRequiredMessage('AUTH_041: x'), isTrue);
    expect(isFaydaStepUpRequiredMessage('AUTH_041: x'), isTrue);
    expect(isFaydaStepUpRequiredMessage('AUTH_040: x'), isFalse);
    expect(isFaydaStepUpRequiredMessage(null), isFalse);
    expect(
      isKycRequiredMessage('ORDER_KYC_REQUIRED: phone not verified'),
      isTrue,
    );
    expect(isKycRequiredMessage('FRAUD_VELOCITY_EXCEEDED'), isFalse);
  });
}
