"""Export RPCs and message attributes from a protoc FileDescriptorSet.
Usage: python export_proto_inventory.py contract.pb > rpc-inventory.json
Requires google.protobuf. Offline only; never connects to a server.
"""
import json
import sys
from pathlib import Path
from google.protobuf import descriptor_pb2 as pb


def export_inventory(descriptor):
    result = {'services': [], 'messages': {}, 'enums': {}}
    for file in descriptor.file:
        comments = {tuple(location.path): (location.leading_comments + location.trailing_comments).strip()
                    for location in file.source_code_info.location}

        def message_record(message, prefix, path):
            full_name = '.'.join(filter(None, [prefix, message.name]))
            fields = []
            for i, field in enumerate(message.field):
                kind = field.type_name.lstrip('.') or pb.FieldDescriptorProto.Type.Name(field.type).removeprefix('TYPE_').lower()
                fields.append({'name': field.name, 'json_name': field.json_name or field.name,
                               'number': field.number, 'type': kind,
                               'cardinality': pb.FieldDescriptorProto.Label.Name(field.label).removeprefix('LABEL_').lower(),
                               'proto3_optional': field.proto3_optional,
                               'oneof': message.oneof_decl[field.oneof_index].name if field.HasField('oneof_index') else None,
                               'comment': comments.get(path + (2, i), '')})
            result['messages'][full_name] = {'fields': fields, 'map_entry': message.options.map_entry,
                                             'comment': comments.get(path, '')}
            for i, nested in enumerate(message.nested_type):
                message_record(nested, full_name, path + (3, i))
            for i, enum in enumerate(message.enum_type):
                enum_record(enum, full_name, path + (4, i))

        def enum_record(enum, prefix, path):
            result['enums']['.'.join(filter(None, [prefix, enum.name]))] = {
                'comment': comments.get(path, ''),
                'values': [{'name': v.name, 'number': v.number, 'comment': comments.get(path + (2, i), '')}
                           for i, v in enumerate(enum.value)]}

        for i, message in enumerate(file.message_type):
            message_record(message, file.package, (4, i))
        for i, enum in enumerate(file.enum_type):
            enum_record(enum, file.package, (5, i))
        for i, service in enumerate(file.service):
            name = '.'.join(filter(None, [file.package, service.name]))
            result['services'].append({'name': name, 'file': file.name, 'comment': comments.get((6, i), ''),
                'methods': [{'name': method.name, 'path': '/' + name + '/' + method.name,
                             'input': method.input_type.lstrip('.'), 'output': method.output_type.lstrip('.'),
                             'client_streaming': method.client_streaming, 'server_streaming': method.server_streaming,
                             'comment': comments.get((6, i, 2, j), '')}
                            for j, method in enumerate(service.method)]})
    return result


if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('Usage: python export_proto_inventory.py contract.pb > rpc-inventory.json')
    descriptor = pb.FileDescriptorSet.FromString(Path(sys.argv[1]).read_bytes())
    print(json.dumps(export_inventory(descriptor), ensure_ascii=False, indent=2))
